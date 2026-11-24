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
    private const string CivilDateTime = "yyyy-MM-dd'T'HH:mm:ss";
    private const string OpeningField = "openingPolicy.opensOn";
    internal const string Welcome = "Seja bem-vindo! Compartilhe uma lembrança deste evento.";
    internal static readonly EventCoverResponse DefaultCover = new("default");

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
        var row = await GetLinkedAsync(accountId, ParseEventId(eventId), cancellationToken);
        return Represent(row, clock.GetUtcNow());
    }

    public async Task<EventOpeningStatusResponse> GetOpeningStatusAsync(
        long accountId, string eventId, CancellationToken cancellationToken)
    {
        var row = await GetLinkedAsync(accountId, ParseEventId(eventId), cancellationToken);
        return EventOpeningService.DescribeContent(row, clock.GetUtcNow());
    }

    public async Task<EventPublicLinkResponse> GetPublicLinkAsync(
        long accountId, string eventId, CancellationToken cancellationToken)
    {
        var row = await GetLinkedAsync(accountId, ParseEventId(eventId), cancellationToken);
        return new EventPublicLinkResponse(row.Code);
    }

    internal static long ParseEventId(string eventId)
    {
        if (!long.TryParse(eventId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0 || Decimal(id) != eventId)
        {
            throw new EventRequestValidationException("eventId");
        }

        return id;
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

        if (candidate.OpensAt is { } opening)
        {
            EventOpeningService.ValidateFutureDate(opening, candidate.SubmissionClosesAt,
                TimeZoneInfo.ConvertTime(now, EventOpeningService.Zone).DateTime, OpeningField);
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

    internal static Guid ParseRequestKey(string? key)
    {
        if (!Guid.TryParseExact(key, "D", out var parsed) || parsed.Version != 4 || (parsed.ToByteArray()[8] & 0xc0) != 0x80)
        {
            throw new EventRequestValidationException("Idempotency-Key");
        }

        return parsed;
    }

    private static byte[] HashKey(string? key) => SHA256.HashData(ParseRequestKey(key).ToByteArray());

    private static EventEntity PrepareCreation(long accountId, CreateEventRequest request, byte[] keyHash)
    {
        var name = ParseName(request.Name);
        var starts = ParseStartsAt(request.StartsAt);
        var welcome = ParseWelcome(request.WelcomeMessage);
        var (opens, closes, deadline) = CalculateSubmissionWindow(starts);
        var policy = request.OpeningPolicy;
        var mode = policy is null ? EventOpeningService.Scheduled
            : EventOpeningService.ValidateMode(policy.Mode, "openingPolicy.mode");
        var opening = mode == EventOpeningService.Scheduled && policy?.OpensOn is null
            ? starts.Date.AddYears(1)
            : EventOpeningService.ParseDate(mode, policy?.OpensOn, OpeningField);
        var canonicalOpening = opening?.ToString(EventOpeningService.DateFormat, CultureInfo.InvariantCulture) ?? mode;
        string[] fingerprint = welcome is null
            ? [name, FormatCivil(starts), canonicalOpening]
            : [name, FormatCivil(starts), canonicalOpening, welcome];
        var canonical = JsonSerializer.SerializeToUtf8Bytes(fingerprint);
        return new EventEntity
        {
            CreatorAccountId = accountId,
            Code = string.Empty,
            Name = name,
            WelcomeMessage = welcome,
            StartsAt = starts,
            SubmissionOpensAt = opens,
            SubmissionClosesAt = closes,
            CompletionDeadlineAt = deadline,
            OpeningMode = mode,
            OpensAt = opening,
            AccessState = EventOpeningService.Flexible,
            CreationKeyHash = keyHash,
            CreationPayloadHash = SHA256.HashData(canonical)
        };
    }

    internal static string ParseName(string? value)
    {
        var name = value?.Trim();
        if (name is null || name.EnumerateRunes().Count() is < 1 or > 120 || name.Contains('\0'))
        {
            throw new EventRequestValidationException("name");
        }

        return name;
    }

    internal static DateTime ParseStartsAt(string? value)
    {
        if (!DateTime.TryParseExact(value, CivilDateTime, CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var starts) || starts < DateTime.MinValue.AddHours(2) || starts.Year == DateTime.MaxValue.Year
            || EventOpeningService.Zone.IsInvalidTime(starts) || EventOpeningService.Zone.IsAmbiguousTime(starts))
        {
            throw new EventRequestValidationException("startsAt");
        }

        return starts;
    }

    internal static string? ParseWelcome(string? value)
    {
        var message = value?.Trim();
        if (message is not null && (message.EnumerateRunes().Count() is < 1 or > 2000 || message.Contains('\0')))
        {
            throw new EventRequestValidationException("welcomeMessage");
        }

        return message;
    }

    internal static (DateTime Opens, DateTime Closes, DateTime Deadline) CalculateSubmissionWindow(DateTime starts)
    {
        var nextDay = starts.Date.AddDays(1);
        return (starts.AddHours(-2), nextDay.AddHours(12), nextDay.AddHours(13));
    }

    internal static EventResponse Represent(EventEntity row, DateTimeOffset now)
    {
        return new EventResponse(
            Decimal(row.Id), Decimal(row.Version), row.Name, FormatCivil(row.StartsAt), EventOpeningService.Brasilia, DefaultCover,
            row.WelcomeMessage ?? Welcome, row.WelcomeMessage is null, FormatCivil(row.SubmissionOpensAt), FormatCivil(row.SubmissionClosesAt),
            FormatCivil(row.CompletionDeadlineAt), EventOpeningService.Describe(row, now),
            row.LockedAt is null, now);
    }

    internal static string FormatCivil(DateTime value) => value.ToString(CivilDateTime, CultureInfo.InvariantCulture);
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
}
