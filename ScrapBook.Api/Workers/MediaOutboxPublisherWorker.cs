using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Workers;

public sealed class MediaOutboxPublisherWorker(IServiceScopeFactory scopes, ILogger<MediaOutboxPublisherWorker> logger)
    : BackgroundService
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
                await Task.WhenAll(batch.Select(row => PublishAsync(row, stoppingToken)));
            }
            catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException)
            {
                logger.LogWarning("Media outbox operation requires reconciliation. {ErrorCode}", "MEDIA_OUTBOX_DATABASE_RETRY");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediaOutboxPublisherService>().ClaimAsync(cancellationToken);
    }

    private async Task PublishAsync(OutboxMessageEntity row, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<MediaOutboxPublisherService>().PublishAsync(row, cancellationToken);
        if (outcome is MediaOutboxPublishResult.Failed or MediaOutboxPublishResult.InvalidFact)
        {
            logger.LogError("Media outbox publication terminated. {Outcome}", outcome);
        }
        else if (outcome == MediaOutboxPublishResult.ReconciliationRequired)
        {
            logger.LogError("Media outbox publication requires reconciliation. {ErrorCode}",
                "MEDIA_PUBLISH_RECONCILIATION_REQUIRED");
        }
        else if (outcome is MediaOutboxPublishResult.RetryScheduled or MediaOutboxPublishResult.LeaseLost)
        {
            logger.LogWarning("Media outbox publication will be reconciled. {Outcome}", outcome);
        }
    }
}
