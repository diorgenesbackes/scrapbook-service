using System.Text.Json;
using ScrapBook.Domain.Entities;

namespace ScrapBook.UnitTests.Services;

internal static class EventStructuralLockTestFixture
{
    private const string FirstConfirmationType = "event.first-record-confirmed.v1";
    internal static readonly Guid MessageId = Guid.Parse("3b0da52a-12ea-1258-ec0b-73e8530e90cf");
    internal static readonly Guid CorrelationId = Guid.Parse("da771942-1b5b-4c4b-885a-62260ad1fc65");
    internal static readonly DateTimeOffset ConfirmedAt = DateTimeOffset.Parse("2031-01-01T00:00:00.120Z");
    internal static readonly DateTime ConfirmedInBrasilia = new(2030, 12, 31, 21, 0, 0, 120);

    internal static string Envelope(string recordId = "237") => JsonSerializer.Serialize(new
    {
        messageId = MessageId,
        type = FirstConfirmationType,
        version = 1,
        eventId = "42",
        occurredAt = "2031-01-01T00:00:00.120Z",
        correlationId = CorrelationId,
        data = new { recordId, confirmedAt = "2031-01-01T00:00:00.120Z" }
    });

    internal static OutboxMessageEntity Origin() => new(MessageId, FirstConfirmationType, 1, "event", 42,
        OriginPayload("237", ConfirmedAt), CorrelationId, ConfirmedAt, 1, null, "published");

    internal static string OriginPayload(string recordId, DateTimeOffset confirmedAt) =>
        JsonSerializer.Serialize(new { eventId = "42", recordId, confirmedAt });

    internal static EventEntity Event(DateTime? lockedAt = null, long? firstRecordId = null) => new()
    {
        Id = 42,
        Version = 7,
        Code = new string('A', 43),
        Name = "Evento sintético",
        StartsAt = new DateTime(2031, 1, 1, 18, 30, 0),
        SubmissionOpensAt = new DateTime(2031, 1, 1, 16, 30, 0),
        SubmissionClosesAt = new DateTime(2031, 1, 2, 12, 0, 0),
        CompletionDeadlineAt = new DateTime(2031, 1, 2, 13, 0, 0),
        OpeningMode = "scheduled",
        AccessState = "flexible",
        AccessVersion = 13,
        OpensAt = new DateTime(2031, 1, 9),
        LockedAt = lockedAt,
        FirstRecordId = firstRecordId,
        FirstRecordConfirmedAt = lockedAt.HasValue ? ConfirmedInBrasilia : null
    };
}
