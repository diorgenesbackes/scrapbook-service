using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

public sealed class MediaOutboxPublisherService(IOutboxRepository outbox, IOutboxPublisher publisher,
    IMediaAdmissionRepository admission, IMediaPreparationRepository preparation, TimeProvider clock)
{
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(8);

    public Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return outbox.ClaimAsync(MediaConfirmationModel.Type, 4, Guid.NewGuid(), now, now + Lease, cancellationToken);
    }

    public async Task<OutboxPublishResult> PublishAsync(OutboxMessageEntity row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (row.LeaseToken is not { } lease) return OutboxPublishResult.LeaseLost;
        var message = MediaConfirmationCodec.ReadOutbox(row);
        var source = message is null ? null : await admission.FindSourceAsync(message, false, cancellationToken);
        if (message is null || !MediaConfirmationService.MatchesSource(message, source))
        {
            return await RegisterFailureAsync(row, lease, null, 0, false, "MEDIA_PUBLISH_INVALID_FACT", cancellationToken);
        }
        if (row.AttemptCount > MaximumAttempts)
        {
            return await RegisterFailureAsync(row, lease, message, source!.ProfileVersion, false,
                "MEDIA_PUBLISH_ATTEMPTS_EXHAUSTED", cancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublishTimeout);
        try
        {
            await publisher.PublishAsync(row.Type, MediaConfirmationCodec.WriteEnvelope(message), timeout.Token);
        }
        catch (OutboxPublishException error)
        {
            return await RegisterFailureAsync(row, lease, message, source!.ProfileVersion, error.Retryable,
                "MEDIA_PUBLISH_TRANSPORT_FAILURE", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return await RegisterFailureAsync(row, lease, message, source!.ProfileVersion, true,
                "MEDIA_PUBLISH_TIMEOUT", cancellationToken);
        }

        return await outbox.MarkPublishedAsync(row.MessageId, lease, clock.GetUtcNow(), cancellationToken)
            ? OutboxPublishResult.Published : OutboxPublishResult.LeaseLost;
    }

    private async Task<OutboxPublishResult> RegisterFailureAsync(OutboxMessageEntity row, Guid lease,
        MediaConfirmationModel? message, int profileVersion, bool retryable, string code, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (retryable && row.AttemptCount < MaximumAttempts)
        {
            var next = now.AddSeconds(Math.Min(30, Math.Pow(2, row.AttemptCount)));
            return await outbox.MarkFailedAsync(row.MessageId, lease, false, code, now, next, cancellationToken)
                ? OutboxPublishResult.RetryScheduled : OutboxPublishResult.LeaseLost;
        }

        await using var transaction = await outbox.BeginAsync(cancellationToken);
        if (!await outbox.MarkFailedAsync(row.MessageId, lease, true, code, now, now, cancellationToken))
        {
            return OutboxPublishResult.LeaseLost;
        }
        if (message is not null)
        {
            await preparation.InsertFailureAsync(MediaFailureService.Failed(message, profileVersion, null, "publish", code, now),
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return message is null ? OutboxPublishResult.InvalidFact : OutboxPublishResult.Failed;
    }
}
