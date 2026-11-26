using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

internal static class StructuralLockOutcomeService
{
    internal const string AppliedType = "event.structural-lock.applied.v1";
    internal const string FailedType = "event.structural-lock.failed.v1";
    internal const string ConsumeExhausted = "CONSUME_ATTEMPTS_EXHAUSTED";
    internal const string ReplayExpired = "CONSUME_REPLAY_EXPIRED";

    internal static StructuralLockOutboxEntity Applied(FirstRecordConfirmedModel message, DateTimeOffset lockedAt) =>
        Outcome(message, AppliedType, "applied", lockedAt, JsonSerializer.Serialize(new
        {
            eventId = message.EventId.ToString(CultureInfo.InvariantCulture),
            confirmedAt = message.ConfirmedAt,
            lockedAt
        }));

    internal static StructuralLockOutboxEntity Failed(FirstRecordConfirmedModel message, DateTimeOffset failedAt,
        string stage, string code) =>
        Outcome(message, FailedType, stage, failedAt, JsonSerializer.Serialize(new
        {
            eventId = message.EventId.ToString(CultureInfo.InvariantCulture),
            confirmedAt = message.ConfirmedAt,
            failedAt,
            stage,
            code
        }));

    internal static async Task RecordTerminalConsumeAsync(IEventStructuralLockRepository locks, IOutboxRepository outbox,
        ProcessedMessageEntity processed, FirstRecordConfirmedModel message, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var code = EventStructuralLockFactService.OutsideReplayHorizon(message, now)
            && processed.ErrorCode != EventStructuralLockFactService.ReplayAuthorized ? ReplayExpired : ConsumeExhausted;
        var failedAt = FirstRecordConfirmedCodec.MicrosecondPrecision(now);
        await locks.QuarantineAsync(processed.Id, code, failedAt, cancellationToken);
        await outbox.InsertResultAsync(Failed(message, failedAt, "consume", code), cancellationToken);
    }

    private static StructuralLockOutboxEntity Outcome(FirstRecordConfirmedModel message, string type, string kind,
        DateTimeOffset occurredAt, string payload)
    {
        var name = type + ":" + message.MessageId.ToString("D") + ":" + kind;
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));
        return new StructuralLockOutboxEntity(id, type, message.EventId, payload, message.CorrelationId,
            message.MessageId, occurredAt);
    }
}
