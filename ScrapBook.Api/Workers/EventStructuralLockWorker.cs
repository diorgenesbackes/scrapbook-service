using ScrapBook.Application.Diagnostics;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Workers;

public sealed class EventStructuralLockWorker(IEventStructuralLockQueue queue, IServiceScopeFactory scopes,
    ILogger<EventStructuralLockWorker> logger) : BackgroundService
{
    private const int Parallelism = 4;
    private const int MaximumReceives = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await ReceiveAsync(stoppingToken);
                await Task.WhenAll(batch.Select(message => ConsumeAsync(message, stoppingToken)));
            }
            catch (EventStructuralLockQueueException)
            {
                logger.LogWarning("Structural lock queue polling unavailable. {ErrorCode}", "LOCK_QUEUE_UNAVAILABLE");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<EventStructuralLockDelivery>> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await queue.ReceiveAsync(Parallelism, deadline.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new EventStructuralLockQueueException(error);
        }
    }

    private async Task ConsumeAsync(EventStructuralLockDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.ReceiveCount > 1)
        {
            EventStructuralLockTelemetry.RecordRedelivery();
        }

        var result = await EvaluateAsync(delivery, false, cancellationToken);
        if ((result is null or EventStructuralLockResult.Rejected) && delivery.ReceiveCount >= MaximumReceives)
        {
            result = await EvaluateAsync(delivery, true, cancellationToken);
        }

        EventStructuralLockTelemetry.RecordConsumption(result);
        switch (result)
        {
            case EventStructuralLockResult.Applied:
            case EventStructuralLockResult.Duplicate:
                await AcknowledgeAsync(delivery.ReceiptHandle, cancellationToken);
                break;
            case EventStructuralLockResult.Terminal:
                logger.LogError(new EventId(4200, "event.structural_lock.failed"),
                    "Structural lock consumption requires recovery. {Stage} {ErrorCode}", "consume", "LOCK_CONSUME_TERMINAL");
                break;
            case EventStructuralLockResult.Rejected:
                logger.LogWarning("Structural lock delivery was rejected. {ErrorCode}", "LOCK_MESSAGE_REJECTED");
                break;
        }
    }

    private async Task<EventStructuralLockResult?> EvaluateAsync(EventStructuralLockDelivery delivery, bool terminal,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<EventStructuralLockService>();
            return terminal ? await service.RecordTerminalDeliveryAsync(delivery.Body, deadline.Token)
                : await service.ConsumeAsync(delivery.Body, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            logger.LogWarning("Structural lock processing exceeded its time budget. {ErrorCode}", "LOCK_PROCESSING_TIMEOUT");
        }
        catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException)
        {
            logger.LogWarning("Structural lock processing requires reconciliation. {ErrorCode}", "LOCK_DELIVERY_RETRY");
        }
        return null;
    }

    private async Task AcknowledgeAsync(string receiptHandle, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await queue.AcknowledgeAsync(receiptHandle, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            logger.LogWarning("Structural lock acknowledgement timed out. {ErrorCode}", "LOCK_ACK_TIMEOUT");
        }
        catch (EventStructuralLockQueueException)
        {
            logger.LogWarning("Structural lock acknowledgement failed. {ErrorCode}", "LOCK_ACK_FAILURE");
        }
    }
}
