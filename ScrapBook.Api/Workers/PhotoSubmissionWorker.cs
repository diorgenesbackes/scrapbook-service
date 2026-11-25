using ScrapBook.Application.Services;

namespace ScrapBook.Api.Workers;

public sealed class PhotoSubmissionWorker(IServiceScopeFactory scopes, ILogger<PhotoSubmissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await WorkAsync(static (service, token) => service.ProcessNextAsync(token), stoppingToken);
            var cleaned = await WorkAsync(static (service, token) => service.CleanNextAsync(token), stoppingToken);
            if (!processed && !cleaned)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<bool> WorkAsync(Func<PhotoSubmissionService, CancellationToken, Task<bool>> work,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<PhotoSubmissionService>();
            return await work(service, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Photo submission work could not be completed. Failure category {FailureCategory}.",
                error.GetType().Name);
            return false;
        }
    }
}
