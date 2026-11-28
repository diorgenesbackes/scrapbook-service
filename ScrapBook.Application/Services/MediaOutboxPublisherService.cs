using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.MediaConfirmationService;

namespace ScrapBook.Application.Services;

public sealed class MediaOutboxPublisherService(IOutboxRepository outbox, IOutboxPublisher publisher,
    IMediaAdmissionRepository admission, IMediaPreparationRepository preparation, TimeProvider clock,
    DateTimeOffset? historicalBefore = null)
{
    private const int MaximumAttempts = 5;
    private const string InvalidFact = "MEDIA_PUBLISH_INVALID_FACT";
    private const string ReconciliationRequired = "MEDIA_PUBLISH_RECONCILIATION_REQUIRED";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(8);

    public Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return outbox.ClaimAsync(MediaConfirmationModel.Type, 4, Guid.NewGuid(), now, now + Lease, historicalBefore, cancellationToken);
    }

    public async Task<MediaOutboxPublishResult> PublishAsync(OutboxMessageEntity row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (row.LeaseToken is not { } lease) return MediaOutboxPublishResult.LeaseLost;
        var message = MediaConfirmationCodec.ReadOutbox(row);
        var source = message is null ? null : await admission.FindSourceAsync(message, false, cancellationToken);
        if (message is null || !MatchesSource(message, source))
        {
            return await RegisterFailureAsync(row, lease, null, false, InvalidFact, cancellationToken);
        }
        if (row.AttemptCount > MaximumAttempts)
        {
            return await RegisterFailureAsync(row, lease, message, false, ReconciliationRequired, cancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublishTimeout);
        try
        {
            await publisher.PublishAsync(row.Type, MediaConfirmationCodec.WriteEnvelope(message), timeout.Token);
        }
        catch (OutboxPublishException error)
        {
            return await RegisterFailureAsync(row, lease, message, error.Retryable,
                "MEDIA_PUBLISH_TRANSPORT_FAILURE", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return await RegisterFailureAsync(row, lease, message, true,
                "MEDIA_PUBLISH_TIMEOUT", cancellationToken);
        }

        return await outbox.MarkPublishedAsync(row.MessageId, lease, clock.GetUtcNow(), cancellationToken)
            ? MediaOutboxPublishResult.Published : MediaOutboxPublishResult.LeaseLost;
    }

    private async Task<MediaOutboxPublishResult> RegisterFailureAsync(OutboxMessageEntity row, Guid lease,
        MediaConfirmationModel? message, bool retryable, string code, CancellationToken cancellationToken)
    {
        if (retryable && row.AttemptCount < MaximumAttempts)
        {
            var retryStartedAt = clock.GetUtcNow();
            var next = retryStartedAt.AddSeconds(Math.Min(30, Math.Pow(2, row.AttemptCount)));
            return await outbox.MarkFailedAsync(row.MessageId, lease, false, code, retryStartedAt, next, cancellationToken)
                ? MediaOutboxPublishResult.RetryScheduled : MediaOutboxPublishResult.LeaseLost;
        }

        await using var transaction = await outbox.BeginAsync(cancellationToken);
        var source = message is null ? null : await admission.FindSourceAsync(message, true, cancellationToken);
        var delivery = message is null || source is null ? null
            : await admission.FindDeliveryAsync(Consumer, Source, message.MessageId, cancellationToken);
        var now = clock.GetUtcNow();
        if (message is not null && MatchesDelivery(delivery, MediaConfirmationCodec.Hash(message)))
        {
            if (!await outbox.MarkPublishedAsync(row.MessageId, lease, now, cancellationToken))
            {
                return MediaOutboxPublishResult.LeaseLost;
            }
            await transaction.CommitAsync(cancellationToken);
            return MediaOutboxPublishResult.Published;
        }

        var terminal = TerminalResult(row, message, source, delivery, retryable);
        var terminalCode = terminal == MediaOutboxPublishResult.ReconciliationRequired ? ReconciliationRequired : code;
        if (!await outbox.MarkFailedAsync(row.MessageId, lease, true, terminalCode, now, now, cancellationToken))
        {
            return MediaOutboxPublishResult.LeaseLost;
        }
        if (terminal == MediaOutboxPublishResult.Failed)
        {
            await preparation.InsertFailureAsync(MediaFailureService.Failed(message!, source!.ProfileVersion, null,
                "publish", terminalCode, now), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return terminal;
    }

    private static MediaOutboxPublishResult TerminalResult(OutboxMessageEntity row, MediaConfirmationModel? message,
        MediaSourceEntity? source, ProcessedMessageEntity? delivery, bool retryable)
    {
        if (message is null) return MediaOutboxPublishResult.InvalidFact;
        return !retryable && row.AttemptCount == 1 && delivery is null && MatchesSource(message, source)
            ? MediaOutboxPublishResult.Failed : MediaOutboxPublishResult.ReconciliationRequired;
    }
}
