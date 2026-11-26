using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

public sealed class OutboxPublisherService(IOutboxRepository repository, IOutboxPublisher publisher, TimeProvider clock)
{
    private const int MaximumAttempts = 5;
    private const int BatchSize = 4;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(8);

    public Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return repository.ClaimAsync(FirstRecordConfirmedModel.Type, BatchSize, Guid.NewGuid(), now, now + Lease, cancellationToken);
    }

    public async Task<OutboxPublishResult> PublishAsync(OutboxMessageEntity row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (row.LeaseToken is not { } lease) return OutboxPublishResult.LeaseLost;
        if (row.AttemptCount > MaximumAttempts)
        {
            return await RegisterFailureAsync(row, lease, false, "PUBLISH_ATTEMPTS_EXHAUSTED", cancellationToken);
        }
        if (row is not { Type: FirstRecordConfirmedModel.Type, ProtocolVersion: FirstRecordConfirmedModel.Version, AggregateType: "event" }
            || FirstRecordConfirmedCodec.ReadOutbox(row) is not { } message)
        {
            return await RegisterFailureAsync(row, lease, false, "PUBLISH_INVALID_FACT", cancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublishTimeout);
        try
        {
            await publisher.PublishAsync(row.Type, FirstRecordConfirmedCodec.WriteEnvelope(message), timeout.Token);
        }
        catch (OutboxPublishException error)
        {
            return await RegisterFailureAsync(row, lease, error.Retryable, "PUBLISH_TRANSPORT_FAILURE", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return await RegisterFailureAsync(row, lease, true, "PUBLISH_TIMEOUT", cancellationToken);
        }

        return await repository.MarkPublishedAsync(row.MessageId, lease, clock.GetUtcNow(), cancellationToken)
            ? OutboxPublishResult.Published : OutboxPublishResult.LeaseLost;
    }

    private async Task<OutboxPublishResult> RegisterFailureAsync(OutboxMessageEntity row, Guid lease, bool retryable,
        string code, CancellationToken cancellationToken)
    {
        var terminal = !retryable || row.AttemptCount >= MaximumAttempts;
        var now = clock.GetUtcNow();
        var availableAt = terminal ? now : now.AddSeconds(Math.Min(30, Math.Pow(2, row.AttemptCount)));
        if (!await repository.MarkFailedAsync(row.MessageId, lease, terminal, code, now, availableAt, cancellationToken))
        {
            return OutboxPublishResult.LeaseLost;
        }
        return terminal ? OutboxPublishResult.Failed : OutboxPublishResult.RetryScheduled;
    }
}
