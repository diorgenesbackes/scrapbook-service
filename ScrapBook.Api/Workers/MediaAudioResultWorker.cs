using ScrapBook.Application.Codecs;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Workers;

public sealed class MediaAudioResultWorker(IMediaAudioResultQueue queue, IServiceScopeFactory scopes,
    ILogger<MediaAudioResultWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            IReadOnlyList<MediaAudioResultDelivery> batch;
            try
            {
                batch = await queue.ReceiveAsync(4, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Audio result polling exceeded its time budget. {ErrorCode}", "MEDIA_AUDIO_QUEUE_TIMEOUT");
                continue;
            }
            catch (MediaAudioResultQueueException)
            {
                logger.LogWarning("Audio result polling unavailable. {ErrorCode}", "MEDIA_AUDIO_QUEUE_UNAVAILABLE");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            await Task.WhenAll(batch.Select(delivery => WakeAsync(delivery, stoppingToken)));
        }
    }

    private async Task WakeAsync(MediaAudioResultDelivery delivery, CancellationToken cancellationToken)
    {
        var callback = MediaAudioCallbackCodec.Read(delivery.Body);
        if (callback is null)
        {
            logger.LogWarning("Audio result delivery rejected. {ErrorCode}", "MEDIA_AUDIO_CALLBACK_REJECTED");
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<MediaAudioPreparationService>();
            await service.WakeAsync(callback, deadline.Token);
            await queue.AcknowledgeAsync(delivery.ReceiptHandle, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Audio result exceeded its time budget. {ErrorCode}", "MEDIA_AUDIO_RESULT_TIMEOUT");
        }
        catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException
            or MediaPreparationWriteException or MediaProviderUnavailableException or MediaAudioResultQueueException)
        {
            logger.LogWarning("Audio result will be redelivered. {ErrorCode}", "MEDIA_AUDIO_RESULT_RETRY");
        }
    }
}
