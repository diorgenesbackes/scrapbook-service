using System.Security.Cryptography;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

public sealed class EventStructuralLockService(IEventWriteRepository events, IInvitationRepository invitations,
    IEventStructuralLockRepository locks, IOutboxRepository outbox, TimeProvider clock)
{
    private const string Consumer = "event-structural-lock";
    private const string Source = "internal";

    public async Task<EventStructuralLockResult> ConsumeAsync(string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = FirstRecordConfirmedCodec.ReadEnvelope(body);
        if (message is null || !await IsVerifiedAsync(message, cancellationToken))
        {
            return EventStructuralLockResult.Rejected;
        }

        await using var transaction = await events.BeginAsync(cancellationToken);
        var hash = FirstRecordConfirmedCodec.Hash(message);
        var processed = await locks.GetOrInsertAsync(Consumer, Source, message.MessageId, hash, message.CorrelationId, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(hash, processed.PayloadHash))
        {
            return EventStructuralLockResult.Rejected;
        }
        if (processed.State == "completed")
        {
            return EventStructuralLockResult.Duplicate;
        }
        if (processed.State != "processing") return EventStructuralLockResult.Rejected;

        var item = await events.FindForUpdateAsync(message.EventId, cancellationToken);
        var confirmedAt = SubmissionService.Civil(FirstRecordConfirmedCodec.MicrosecondPrecision(message.ConfirmedAt));
        if (item is null || await locks.FindRecordConfirmationAsync(message.EventId, message.RecordId, cancellationToken) != confirmedAt)
        {
            return EventStructuralLockResult.Rejected;
        }

        var pending = item.LockedAt is null ? await FindPendingForUpdateAsync(item.Id, cancellationToken) : null;
        var now = clock.GetUtcNow();
        var result = EventStructuralLockResult.Duplicate;
        if (item.LockedAt is null)
        {
            await ApplyAsync(item, pending, message, confirmedAt, now, cancellationToken);
            result = EventStructuralLockResult.Applied;
        }
        else if (item.FirstRecordId != message.RecordId || item.FirstRecordConfirmedAt != confirmedAt)
        {
            return EventStructuralLockResult.Rejected;
        }
        await locks.CompleteAsync(processed.Id, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<bool> IsVerifiedAsync(FirstRecordConfirmedModel message, CancellationToken cancellationToken)
    {
        if (message.MessageId != SubmissionService.FirstMessageId(message.EventId))
        {
            return false;
        }
        var original = await outbox.FindAsync(message.MessageId, cancellationToken);
        return original is { AggregateType: "event", Type: FirstRecordConfirmedModel.Type, ProtocolVersion: FirstRecordConfirmedModel.Version }
            && FirstRecordConfirmedCodec.ReadOutbox(original) == message;
    }

    private async Task<InvitationEntity?> FindPendingForUpdateAsync(long eventId, CancellationToken cancellationToken)
    {
        var pending = await invitations.FindPendingAsync(eventId, cancellationToken);
        return pending is null ? null
            : await invitations.FindForUpdateAsync(new InvitationLocator(pending.Id, eventId), cancellationToken);
    }

    private async Task ApplyAsync(EventEntity item, InvitationEntity? invitation, FirstRecordConfirmedModel message,
        DateTime confirmedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await locks.ApplyAsync(new EventStructuralLockCommand(item.Id, item.Version + 1, message.RecordId, confirmedAt, now),
            cancellationToken);
        if (invitation?.State == "pending")
        {
            invitation.State = "invalidated";
            invitation.InvalidatedAt = now;
            invitation.UpdatedAt = now;
            invitation.TokenCiphertext = null;
            invitation.EncryptionKeyId = null;
            invitation.Version++;
            await invitations.SaveClosedAsync(invitation, cancellationToken);
        }
        await events.InsertAuditAsync(new AuditEntryEntity(null, item.Id, "system", "event.structural_lock.applied",
            "event", "success", message.CorrelationId, now, now.AddDays(180), """{"locked":false}""", """{"locked":true}""", "events"),
            cancellationToken);
    }
}
