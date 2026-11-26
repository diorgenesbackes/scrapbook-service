using ScrapBook.Application.Codecs;
using ScrapBook.Application.Diagnostics;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.EventStructuralLockFactService;

namespace ScrapBook.Application.Services;

public sealed class EventStructuralLockService(IEventWriteRepository events, IInvitationRepository invitations,
    IEventStructuralLockRepository locks, IOutboxRepository outbox, TimeProvider clock)
{
    public Task<EventStructuralLockResult> ConsumeAsync(string body, CancellationToken cancellationToken) =>
        ProcessAsync(body, false, cancellationToken);

    public Task<EventStructuralLockResult> RecordTerminalDeliveryAsync(string body, CancellationToken cancellationToken) =>
        ProcessAsync(body, true, cancellationToken);

    private async Task<EventStructuralLockResult> ProcessAsync(string body, bool terminal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = FirstRecordConfirmedCodec.ReadEnvelope(body);
        if (message is null || message.MessageId != SubmissionService.FirstMessageId(message.EventId)
            || ReadOriginal(await outbox.FindAsync(message.MessageId, cancellationToken)) != message)
        {
            return EventStructuralLockResult.Rejected;
        }

        await using var transaction = await events.BeginAsync(cancellationToken);
        var hash = FirstRecordConfirmedCodec.Hash(message);
        var processed = await locks.GetOrInsertAsync(Consumer, Source, message.MessageId, hash, message.CorrelationId, cancellationToken);
        if (!MatchesHash(processed, hash))
        {
            return EventStructuralLockResult.Rejected;
        }
        if (processed.State == "completed")
        {
            return EventStructuralLockResult.Duplicate;
        }
        if (processed.State != "processing")
        {
            return EventStructuralLockResult.Rejected;
        }

        var item = await events.FindForUpdateAsync(message.EventId, cancellationToken);
        if (item is null || !AcceptsLock(item, message)
            || !await HasConfirmedRecordAsync(locks, message, cancellationToken))
        {
            return EventStructuralLockResult.Rejected;
        }

        var now = clock.GetUtcNow();
        if (item.LockedAt is not null)
        {
            await locks.CompleteAsync(processed.Id, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EventStructuralLockResult.Duplicate;
        }
        if (terminal)
        {
            await StructuralLockOutcomeService.RecordTerminalConsumeAsync(locks, outbox, processed, message, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EventStructuralLockResult.Terminal;
        }
        if (OutsideReplayHorizon(message, now) && processed.ErrorCode != ReplayAuthorized)
        {
            return EventStructuralLockResult.Rejected;
        }

        var lockedAt = FirstRecordConfirmedCodec.MicrosecondPrecision(now);
        await ApplyAsync(item, message, lockedAt, cancellationToken);
        await locks.CompleteAsync(processed.Id, lockedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        EventStructuralLockTelemetry.RecordLock((clock.GetUtcNow() - message.ConfirmedAt).TotalSeconds);
        return EventStructuralLockResult.Applied;
    }

    private async Task<InvitationEntity?> FindPendingForUpdateAsync(long eventId, CancellationToken cancellationToken)
    {
        var pending = await invitations.FindPendingAsync(eventId, cancellationToken);
        return pending is null ? null
            : await invitations.FindForUpdateAsync(new InvitationLocator(pending.Id, eventId), cancellationToken);
    }

    private async Task ApplyAsync(EventEntity item, FirstRecordConfirmedModel message, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var invitation = await FindPendingForUpdateAsync(item.Id, cancellationToken);
        await locks.ApplyAsync(new EventStructuralLockCommand(item.Id, item.Version + 1, message.RecordId,
            CivilConfirmation(message), now), cancellationToken);
        if (invitation?.State == "pending")
        {
            await InvalidateAsync(invitation, now, cancellationToken);
        }
        await events.InsertAuditAsync(new AuditEntryEntity(null, item.Id, "system", "event.structural_lock.applied",
            "event", "success", message.CorrelationId, now, now.AddDays(180), """{"locked":false}""", """{"locked":true}""", "events"),
            cancellationToken);
        await outbox.InsertResultAsync(StructuralLockOutcomeService.Applied(message, now), cancellationToken);
    }

    private Task InvalidateAsync(InvitationEntity invitation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        invitation.State = "invalidated";
        invitation.InvalidatedAt = now;
        invitation.UpdatedAt = now;
        invitation.TokenCiphertext = null;
        invitation.EncryptionKeyId = null;
        invitation.Version++;
        return invitations.SaveClosedAsync(invitation, cancellationToken);
    }
}
