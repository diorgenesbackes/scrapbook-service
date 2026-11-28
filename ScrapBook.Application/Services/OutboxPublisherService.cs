using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.EventStructuralLockFactService;

namespace ScrapBook.Application.Services;

public sealed class OutboxPublisherService(IOutboxRepository repository, IOutboxPublisher publisher,
    IEventStructuralLockRepository locks, TimeProvider clock)
{
    private const int MaximumAttempts = 5;
    private const int BatchSize = 4;
    private const string InvalidFact = "PUBLISH_INVALID_FACT";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(8);

    public Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return repository.ClaimAsync(FirstRecordConfirmedModel.Type, BatchSize, Guid.NewGuid(), now, now + Lease, null, cancellationToken);
    }

    public async Task<OutboxPublishResult> PublishAsync(OutboxMessageEntity row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (row.LeaseToken is not { } lease) return OutboxPublishResult.LeaseLost;
        if (row.AttemptCount > MaximumAttempts)
        {
            return await RegisterTerminalAsync(row.MessageId, lease, "PUBLISH_ATTEMPTS_EXHAUSTED", cancellationToken);
        }
        if (ReadOriginal(row) is not { } message || !await HasConfirmedRecordAsync(locks, message, cancellationToken))
        {
            var now = clock.GetUtcNow();
            return await repository.MarkFailedAsync(row.MessageId, lease, true, InvalidFact, now, now, cancellationToken)
                ? OutboxPublishResult.InvalidFact : OutboxPublishResult.LeaseLost;
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!retryable || row.AttemptCount >= MaximumAttempts)
        {
            return await RegisterTerminalAsync(row.MessageId, lease, code, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var availableAt = now.AddSeconds(Math.Min(30, Math.Pow(2, row.AttemptCount)));
        return await repository.MarkFailedAsync(row.MessageId, lease, false, code, now, availableAt, cancellationToken)
            ? OutboxPublishResult.RetryScheduled : OutboxPublishResult.LeaseLost;
    }

    private async Task<OutboxPublishResult> RegisterTerminalAsync(Guid messageId, Guid lease, string code,
        CancellationToken cancellationToken)
    {
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var current = await repository.FindForUpdateAsync(messageId, cancellationToken);
        if (current is not { State: "publishing" } || current.LeaseToken != lease)
        {
            return OutboxPublishResult.LeaseLost;
        }

        var message = ReadOriginal(current);
        if (message is not null && !await HasConfirmedRecordAsync(locks, message, cancellationToken))
        {
            message = null;
        }
        var now = FirstRecordConfirmedCodec.MicrosecondPrecision(clock.GetUtcNow());
        if (!await repository.MarkFailedAsync(messageId, lease, true, message is null ? InvalidFact : code, now, now, cancellationToken))
        {
            return OutboxPublishResult.LeaseLost;
        }
        if (message is not null)
        {
            await repository.InsertResultAsync(StructuralLockOutcomeService.Failed(message, now, "publish", code), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return message is null ? OutboxPublishResult.InvalidFact : OutboxPublishResult.Failed;
    }
}
