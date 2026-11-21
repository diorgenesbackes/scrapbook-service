using System.Globalization;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class EventOpeningService(IEventWriteRepository repository, TimeProvider clock)
{
    internal const string Brasilia = "America/Sao_Paulo";
    internal const string DateFormat = "yyyy-MM-dd";
    internal const string Immediate = "immediate";
    internal const string Scheduled = "scheduled";
    internal const string Flexible = "flexible";
    private const string ScheduledLocked = "scheduled_locked";
    private const string ImmediateOnce = "immediate_once";
    internal static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(Brasilia);

    public async Task<EventResponse> ReplaceAsync(long accountId, string eventId, ReplaceEventOpeningRequest request,
        Guid correlationId, CancellationToken cancellationToken)
    {
        var id = EventService.ParseEventId(eventId);
        var expectedVersion = ParseVersion(request.ExpectedVersion);
        var mode = ValidateMode(request.Mode, "mode");
        var date = ParseDate(mode, request.OpensOn, "opensOn");

        await using var transaction = await repository.BeginAsync(cancellationToken);
        var item = await repository.FindLinkedForUpdateAsync(accountId, id, cancellationToken)
            ?? throw new EventNotFoundException();

        var now = clock.GetUtcNow();
        if (item.Version != expectedVersion)
        {
            throw new EventVersionConflictException();
        }

        if (item.OpeningMode == mode && item.OpensAt == date)
        {
            return EventService.Represent(item, now);
        }

        var local = TimeZoneInfo.ConvertTime(now, Zone).DateTime;
        var state = EffectiveState(item, local);
        if (state == ScheduledLocked)
        {
            throw new EventOpeningLockedException();
        }

        if (date is { } opening)
        {
            ValidateFutureDate(opening, item.SubmissionClosesAt, local, "opensOn");
        }

        var before = AuditState(item);
        Apply(item, mode, date, state, local);
        var audit = new AuditEntryEntity(accountId, id, "account", "event.opening.changed", "event",
            "success", correlationId, now, now.AddDays(180), before, AuditState(item), "events");
        await repository.SaveOpeningAsync(item, now, cancellationToken);
        await repository.InsertAuditAsync(audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EventService.Represent(item, now);
    }

    internal static string ValidateMode(string? mode, string field) => mode is Immediate or Scheduled
        ? mode : throw new EventRequestValidationException(field);

    internal static DateTime? ParseDate(string mode, string? date, string field)
    {
        if (mode == Immediate && date is null)
        {
            return null;
        }

        if (mode == Scheduled && DateTime.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new EventRequestValidationException(field);
    }

    internal static void ValidateFutureDate(DateTime date, DateTime closesAt, DateTime localNow, string field)
    {
        if (date.Date <= localNow.Date || date <= closesAt)
        {
            throw new EventRequestValidationException(field);
        }
    }

    internal static EventOpeningResponse Describe(EventEntity item, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone).DateTime;
        var state = EffectiveState(item, local);
        return new EventOpeningResponse(item.OpeningMode, FormatDate(item.OpensAt), state,
            DescribeContent(item, now).ContentState, state != ScheduledLocked);
    }

    internal static EventOpeningStatusResponse DescribeContent(EventEntity item, DateTimeOffset now)
    {
        var opensAt = item.OpeningMode == Scheduled && item.OpensAt is { } date ? OpeningInstant(date) : (DateTimeOffset?)null;
        return new EventOpeningStatusResponse(opensAt > now ? "closed" : "open", FormatDate(item.OpensAt), opensAt, now);
    }

    private static DateTimeOffset OpeningInstant(DateTime date)
    {
        var offset = Zone.IsInvalidTime(date) ? Zone.GetUtcOffset(date.AddDays(-1))
            : Zone.IsAmbiguousTime(date) ? Zone.GetAmbiguousTimeOffsets(date).Max()
            : Zone.GetUtcOffset(date);
        return new DateTimeOffset(date, offset).ToUniversalTime();
    }

    internal static long ParseVersion(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 0
            ? version : throw new EventRequestValidationException("expectedVersion");

    internal static void PreserveFreeze(EventEntity item, DateTime local, DateTime newCloses)
    {
        var cutoff = local >= item.SubmissionClosesAt ? item.SubmissionClosesAt : newCloses;
        var state = EffectiveState(item, local, cutoff);
        if (state != item.AccessState)
        {
            item.AccessState = state;
            item.AccessFrozenAt = cutoff;
        }
    }

    private static string EffectiveState(EventEntity item, DateTime local) =>
        EffectiveState(item, local, item.SubmissionClosesAt);

    private static string EffectiveState(EventEntity item, DateTime local, DateTime cutoff) =>
        item.AccessState != Flexible || local < cutoff ? item.AccessState
            : item.OpeningMode == Scheduled ? ScheduledLocked : ImmediateOnce;

    private static void Apply(EventEntity item, string mode, DateTime? date, string state, DateTime local)
    {
        item.OpeningMode = mode;
        item.OpensAt = date;
        if (state == ImmediateOnce)
        {
            item.AccessState = ScheduledLocked;
            item.AccessFrozenAt = local;
        }

        item.Version++;
        item.AccessVersion++;
    }

    private static string AuditState(EventEntity item) => JsonSerializer.Serialize(new
    {
        mode = item.OpeningMode,
        opensOn = FormatDate(item.OpensAt),
        version = item.AccessVersion.ToString(CultureInfo.InvariantCulture)
    });

    private static string? FormatDate(DateTime? value) => value?.ToString(DateFormat, CultureInfo.InvariantCulture);
}
