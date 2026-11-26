using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Workers;

public sealed class EventStructuralLockWorker(IEventStructuralLockQueue queue, IServiceScopeFactory scopes,
    ILogger<EventStructuralLockWorker> logger) : BackgroundService
{
    private const int Parallelism = 4;

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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<EventStructuralLockService>();
            var result = await service.ConsumeAsync(delivery.Body, deadline.Token);
            if (result == EventStructuralLockResult.Rejected)
            {
                logger.LogWarning("Structural lock delivery was rejected. {ErrorCode}", "LOCK_MESSAGE_REJECTED");
                return;
            }
            await queue.AcknowledgeAsync(delivery.ReceiptHandle, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            logger.LogWarning("Structural lock processing exceeded its time budget. {ErrorCode}", "LOCK_PROCESSING_TIMEOUT");
        }
        catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException or EventStructuralLockQueueException)
        {
            logger.LogWarning("Structural lock delivery will be retried. {ErrorCode}", "LOCK_DELIVERY_RETRY");
        }
    }
}
