using ScrapBook.Application.Diagnostics;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Workers;

public sealed class OutboxPublisherWorker(IServiceScopeFactory scopes, ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await ClaimAsync(stoppingToken);
                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                    continue;
                }
                await Task.WhenAll(batch.Select(item => PublishAsync(item, stoppingToken)));
            }
            catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException)
            {
                logger.LogWarning("Outbox database operation requires reconciliation. {ErrorCode}", "OUTBOX_DATABASE_RETRY");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxPublisherService>().ClaimAsync(cancellationToken);
    }

    private async Task PublishAsync(OutboxMessageEntity item, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<OutboxPublisherService>().PublishAsync(item, cancellationToken);
        EventStructuralLockTelemetry.RecordPublication(result.ToString());
        if (result == OutboxPublishResult.Failed)
        {
            logger.LogError(new EventId(4200, "event.structural_lock.failed"),
                "Structural lock publication requires recovery. {Stage} {ErrorCode}", "publish", "OUTBOX_PUBLISH_TERMINAL");
        }
        else if (result == OutboxPublishResult.InvalidFact)
        {
            logger.LogWarning("Outbox first confirmation has no verified origin. {ErrorCode}", "PUBLISH_INVALID_FACT");
        }
        else if (result is OutboxPublishResult.RetryScheduled or OutboxPublishResult.LeaseLost)
        {
            logger.LogWarning("Outbox publication will be reconciled. {Outcome}", result);
        }
    }
}
