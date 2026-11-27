using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Api.Workers;

public sealed class MediaPreparationWorker(IServiceScopeFactory scopes, ILogger<MediaPreparationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await Task.WhenAll(RunAsync(workers), RunAsync(workers));
    }

    private async Task RunAsync(CancellationTokenSource workers)
    {
        var cancellationToken = workers.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<MediaPreparationService>();
                    if (!await service.PrepareNextAsync(cancellationToken))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    }
                }
                catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException
                    or MediaPreparationWriteException)
                {
                    logger.LogWarning("Photo preparation requires reconciliation. {ErrorCode}", "MEDIA_PREPARATION_DATABASE_RETRY");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
        finally
        {
            await workers.CancelAsync();
        }
    }
}
