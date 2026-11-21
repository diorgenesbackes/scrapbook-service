using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class EventService(IEventRepository repository, IEventCursorCodec cursors, TimeProvider clock)
{
    private const string Brasilia = "America/Sao_Paulo";
    private const string CivilDateTime = "yyyy-MM-dd'T'HH:mm:ss";
    private const string DateFormat = "yyyy-MM-dd";
    private const string Scheduled = "scheduled";
    private const string Flexible = "flexible";
    private const string OpeningField = "openingPolicy.opensOn";
    private const string Welcome = "Seja bem-vindo! Compartilhe uma lembrança deste evento.";
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(Brasilia);
    private static readonly EventCoverResponse DefaultCover = new("default");

    public async Task<EventListResponse> ListAsync(
        long accountId, string? cursor, string? pageSize, CancellationToken cancellationToken)
    {
        var size = ParsePageSize(pageSize);
        long? boundary = cursor is null ? null
            : cursors.Decode(cursor, accountId) ?? throw new EventRequestValidationException("cursor");
        var rows = await repository.ListLinkedAsync(accountId, boundary, size + 1, cancellationToken);
        var page = rows.Take(size).Select(row => new EventListItemResponse(
            Decimal(row.Id), row.Name, FormatCivil(row.StartsAt), DefaultCover)).ToArray();
        return new EventListResponse(page, rows.Count > size ? cursors.Encode(accountId, rows[size - 1].Id) : null);
    }

    public async Task<EventResponse> GetAsync(long accountId, string eventId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(eventId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0 || Decimal(id) != eventId)
        {
            throw new EventRequestValidationException("eventId");
        }

        var row = await GetLinkedAsync(accountId, id, cancellationToken);
        return Represent(row, clock.GetUtcNow());
    }

    public async Task<CreateEventResponse> CreateAsync(
        long accountId, string? key, CreateEventRequest request, CancellationToken cancellationToken)
    {
        var keyHash = HashKey(key);
        var now = clock.GetUtcNow();
        var candidate = PrepareCreation(accountId, request, keyHash);
        var prior = await repository.FindCreationAsync(accountId, keyHash, cancellationToken);
        if (prior is not null)
        {
            return await ReplayAsync(accountId, prior, candidate, now, cancellationToken);
        }

        if (candidate.OpensAt is not { } opening
            || opening.Date <= TimeZoneInfo.ConvertTime(now, Zone).Date
            || opening <= candidate.SubmissionClosesAt)
        {
            throw new EventRequestValidationException(OpeningField);
        }

        var member = new EventMemberEntity { AccountId = accountId, Slot = 1, JoinedAt = now, Event = candidate };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                candidate.Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                await repository.InsertAsync(candidate, member, cancellationToken);
                return new CreateEventResponse(Represent(candidate, now), false);
            }
            catch (EventCreationKeyCollisionException)
            {
                var winner = await repository.FindCreationAsync(accountId, keyHash, cancellationToken)
                    ?? throw new PostgresUnavailableException();
                return await ReplayAsync(accountId, winner, candidate, now, cancellationToken);
            }
            catch (EventCodeCollisionException error) when (attempt == 2)
            {
                throw new PostgresUnavailableException(error);
            }
            catch (EventCodeCollisionException)
            {
            }
        }

        throw new PostgresUnavailableException();
    }

    private async Task<EventEntity> GetLinkedAsync(long accountId, long eventId, CancellationToken cancellationToken) =>
        await repository.FindLinkedAsync(accountId, eventId, cancellationToken) ?? throw new EventNotFoundException();

    private async Task<CreateEventResponse> ReplayAsync(
        long accountId, EventEntity prior, EventEntity candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var linked = await GetLinkedAsync(accountId, prior.Id, cancellationToken);
        if (prior.CreationPayloadHash is not { } previousHash || candidate.CreationPayloadHash is not { } currentHash
            || !CryptographicOperations.FixedTimeEquals(previousHash, currentHash))
        {
            throw new EventCreationKeyReusedException();
        }

        return new CreateEventResponse(Represent(linked, now), true);
    }

    private static int ParsePageSize(string? pageSize)
    {
        if (pageSize is null)
        {
            return 20;
        }

        return int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 50
            ? size : throw new EventRequestValidationException("pageSize");
    }

    private static byte[] HashKey(string? key)
    {
        if (!Guid.TryParseExact(key, "D", out var parsed) || parsed.Version != 4 || (parsed.ToByteArray()[8] & 0xc0) != 0x80)
        {
            throw new EventRequestValidationException("Idempotency-Key");
        }

        return SHA256.HashData(parsed.ToByteArray());
    }

    private static EventEntity PrepareCreation(long accountId, CreateEventRequest request, byte[] keyHash)
    {
        var name = request.Name?.Trim();
        if (name is null || name.EnumerateRunes().Count() is < 1 or > 120 || name.Contains('\0'))
        {
            throw new EventRequestValidationException("name");
        }

        if (!DateTime.TryParseExact(request.StartsAt, CivilDateTime, CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var starts) || starts < DateTime.MinValue.AddHours(2) || starts.Year == DateTime.MaxValue.Year
            || Zone.IsInvalidTime(starts) || Zone.IsAmbiguousTime(starts))
        {
            throw new EventRequestValidationException("startsAt");
        }

        var opening = ParseOpening(request.OpeningPolicy, starts);
        var nextDay = starts.Date.AddDays(1);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new[] { name, FormatCivil(starts), opening.ToString(DateFormat, CultureInfo.InvariantCulture) });
        return new EventEntity
        {
            CreatorAccountId = accountId,
            Code = string.Empty,
            Name = name,
            StartsAt = starts,
            SubmissionOpensAt = starts.AddHours(-2),
            SubmissionClosesAt = nextDay.AddHours(12),
            CompletionDeadlineAt = nextDay.AddHours(13),
            OpeningMode = Scheduled,
            OpensAt = opening,
            AccessState = Flexible,
            CreationKeyHash = keyHash,
            CreationPayloadHash = SHA256.HashData(canonical)
        };
    }

    private static DateTime ParseOpening(CreateEventOpeningRequest? policy, DateTime starts)
    {
        if (policy is not null && policy.Mode != Scheduled)
        {
            throw new EventRequestValidationException("openingPolicy.mode");
        }

        if (policy?.OpensOn is not { } date)
        {
            return starts.Date.AddYears(1);
        }

        return DateTime.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value : throw new EventRequestValidationException(OpeningField);
    }

    private static EventResponse Represent(EventEntity row, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone).DateTime;
        var state = row.AccessState;
        if (state == Flexible && local >= row.SubmissionClosesAt)
        {
            state = row.OpeningMode == Scheduled ? "scheduled_locked" : "immediate_once";
        }

        var closed = row.OpeningMode == Scheduled && row.OpensAt > local;
        return new EventResponse(
            Decimal(row.Id), Decimal(row.Version), row.Name, FormatCivil(row.StartsAt), Brasilia, DefaultCover,
            row.WelcomeMessage ?? Welcome, FormatCivil(row.SubmissionOpensAt), FormatCivil(row.SubmissionClosesAt),
            FormatCivil(row.CompletionDeadlineAt), new EventOpeningResponse(
                row.OpeningMode, row.OpensAt?.ToString(DateFormat, CultureInfo.InvariantCulture), state,
                closed ? "closed" : "open", state != "scheduled_locked"),
            row.LockedAt is null, now);
    }

    private static string FormatCivil(DateTime value) => value.ToString(CivilDateTime, CultureInfo.InvariantCulture);
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
}
