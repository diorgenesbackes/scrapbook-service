using System.Text.Json;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.EventStructuralLockFactService;

namespace ScrapBook.Application.Services;

public sealed class EventStructuralLockRecoveryService(IEventWriteRepository events, IEventStructuralLockRepository locks,
    IOutboxRepository outbox, TimeProvider clock)
{
    public async Task<EventStructuralLockRecoveryResult> RecoverAsync(EventStructuralLockRecoveryCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(command))
        {
            return EventStructuralLockRecoveryResult.InvalidArguments;
        }

        await using var transaction = await events.BeginAsync(cancellationToken);
        var original = await outbox.FindForUpdateAsync(command.MessageId, cancellationToken);
        if (original is null || ReadOriginal(original) is not { } message || !AcceptsEvidence(command, message))
        {
            return EventStructuralLockRecoveryResult.Rejected;
        }

        var now = clock.GetUtcNow();
        var expired = OutsideReplayHorizon(message, now);
        var hash = FirstRecordConfirmedCodec.Hash(message);
        var processed = await locks.FindForUpdateAsync(Consumer, Source, message.MessageId, cancellationToken);
        if (processed is null && NeedsProtection(command, original.State, expired))
        {
            processed = await locks.GetOrInsertAsync(Consumer, Source, message.MessageId, hash, message.CorrelationId, cancellationToken);
        }
        if (processed is not null && !MatchesHash(processed, hash))
        {
            return EventStructuralLockRecoveryResult.Rejected;
        }

        var item = await events.FindForUpdateAsync(message.EventId, cancellationToken);
        if (item is null || !AcceptsLock(item, message) || !await HasConfirmedRecordAsync(locks, message, cancellationToken))
        {
            return EventStructuralLockRecoveryResult.Rejected;
        }

        var locked = item.LockedAt is not null;
        if (locked && (command.Mode != EventStructuralLockRecoveryMode.Publish || original.State != "failed"))
        {
            return EventStructuralLockRecoveryResult.AlreadyCompleted;
        }
        if ((!locked && processed is { State: "completed" }) || (expired
            && command.Mode != EventStructuralLockRecoveryMode.ReconcileDlq && !command.ReconcileExpired))
        {
            return EventStructuralLockRecoveryResult.Rejected;
        }

        var result = command.Mode == EventStructuralLockRecoveryMode.ReconcileDlq
            ? await ReconcileDlqAsync(original, processed, message, now, cancellationToken)
            : await ReopenAsync(command, original, processed, now, expired && !locked, cancellationToken);
        if (result is EventStructuralLockRecoveryResult.Reconciled or EventStructuralLockRecoveryResult.Reopened)
        {
            await RecordOperationAsync(command, message, result, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return result;
    }

    private async Task<EventStructuralLockRecoveryResult> ReconcileDlqAsync(OutboxMessageEntity original,
        ProcessedMessageEntity? processed, FirstRecordConfirmedModel message, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (original.State is not ("published" or "failed"))
        {
            return EventStructuralLockRecoveryResult.Rejected;
        }
        if (processed is { State: "quarantined" })
        {
            return EventStructuralLockRecoveryResult.AlreadyTerminal;
        }
        if (processed is not { State: "processing" })
        {
            return EventStructuralLockRecoveryResult.Rejected;
        }

        await StructuralLockOutcomeService.RecordTerminalConsumeAsync(locks, outbox, processed, message, now, cancellationToken);
        return EventStructuralLockRecoveryResult.Reconciled;
    }

    private async Task<EventStructuralLockRecoveryResult> ReopenAsync(EventStructuralLockRecoveryCommand command,
        OutboxMessageEntity original, ProcessedMessageEntity? processed, DateTimeOffset now, bool expired,
        CancellationToken cancellationToken)
    {
        var rearming = command.Mode == EventStructuralLockRecoveryMode.Consume;
        if (processed is { State: "quarantined" } && (!rearming || !command.Rearm))
        {
            return EventStructuralLockRecoveryResult.Rejected;
        }
        if (original.State is "pending" or "publishing")
        {
            return processed is { State: "quarantined" }
                ? EventStructuralLockRecoveryResult.Rejected : EventStructuralLockRecoveryResult.AlreadyScheduled;
        }
        if (rearming
            ? original.State is not ("failed" or "published") || processed is not { State: "quarantined" }
            : original.State != "failed")
        {
            return EventStructuralLockRecoveryResult.Rejected;
        }

        if (processed is not null && (rearming || requiresReplayAuthorization))
        {
            await locks.RearmAsync(processed.Id, requiresReplayAuthorization ? ReplayAuthorized : null, now, cancellationToken);
        }
        await outbox.ReopenAsync(original.MessageId, now, cancellationToken);
        return EventStructuralLockRecoveryResult.Reopened;
    }

    private Task RecordOperationAsync(EventStructuralLockRecoveryCommand command, FirstRecordConfirmedModel message,
        EventStructuralLockRecoveryResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var action = result == EventStructuralLockRecoveryResult.Reconciled
            ? "event.structural_lock.dlq_reconciled" : "event.structural_lock.recovery_scheduled";
        var evidence = JsonSerializer.Serialize(new
        {
            messageId = message.MessageId,
            incident = command.Incident,
            candidateSha = command.CandidateSha.ToLowerInvariant(),
            operation = command.Mode switch
            {
                EventStructuralLockRecoveryMode.Publish => "publish",
                EventStructuralLockRecoveryMode.Consume => "consume",
                _ => "reconcile-dlq"
            },
            command.Rearm,
            command.ReconcileExpired
        });
        return events.InsertAuditAsync(new AuditEntryEntity(null, message.EventId, "system", action,
            "event", "success", message.CorrelationId, now, now.AddDays(180), "{}", evidence, "events"), cancellationToken);
    }

    private static bool AcceptsEvidence(EventStructuralLockRecoveryCommand command, FirstRecordConfirmedModel message) =>
        command.Mode != EventStructuralLockRecoveryMode.ReconcileDlq
            || FirstRecordConfirmedCodec.ReadEnvelope(command.Evidence!) == message;

    private static bool NeedsProtection(EventStructuralLockRecoveryCommand command, string originalState, bool expired) =>
        command.Mode == EventStructuralLockRecoveryMode.ReconcileDlq && originalState is "published" or "failed"
        || command.Mode == EventStructuralLockRecoveryMode.Publish && expired && command.ReconcileExpired && originalState == "failed";

    public static bool IsValid(EventStructuralLockRecoveryCommand command) =>
        command.MessageId != Guid.Empty
        && command.Incident is { Length: >= 5 and <= 64 }
        && command.Incident.StartsWith("INC-", StringComparison.Ordinal)
        && char.IsAsciiLetterOrDigit(command.Incident[4])
        && !command.Incident.AsSpan(4).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")
        && command.CandidateSha is { Length: 40 }
        && command.CandidateSha.All(char.IsAsciiHexDigit)
        && command.CandidateSha.Any(character => character != '0')
        && command.Mode switch
        {
            EventStructuralLockRecoveryMode.Publish => !command.Rearm && command.Evidence is null,
            EventStructuralLockRecoveryMode.Consume => command.Evidence is null,
            EventStructuralLockRecoveryMode.ReconcileDlq => !command.Rearm && !command.ReconcileExpired
                && !string.IsNullOrWhiteSpace(command.Evidence),
            _ => false
        };
}
