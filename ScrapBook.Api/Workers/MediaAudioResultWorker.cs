using System.Diagnostics;
using ScrapBook.Application.Codecs;
using ScrapBook.Application.Diagnostics;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Workers;

public sealed class MediaAudioResultWorker(IMediaAudioResultQueue queue, IServiceScopeFactory scopes,
    ILogger<MediaAudioResultWorker> logger, MediaProcessingTelemetry? telemetry = null) : BackgroundService
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
                logger.LogWarning("Media transcoding result polling exceeded its time budget. {ErrorCode}", "MEDIA_AUDIO_QUEUE_TIMEOUT");
                continue;
            }
            catch (MediaAudioResultQueueException)
            {
                logger.LogWarning("Media transcoding result polling unavailable. {ErrorCode}", "MEDIA_AUDIO_QUEUE_UNAVAILABLE");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            await Task.WhenAll(batch.Select(delivery => WakeAsync(delivery, stoppingToken)));
        }
    }

    private async Task WakeAsync(MediaAudioResultDelivery delivery, CancellationToken cancellationToken)
    {
        using var activity = MediaProcessingTelemetry.StartActivity(MediaProcessingStage.ResultConsumption, ActivityKind.Consumer);
        var started = Stopwatch.GetTimestamp();
        var callback = MediaAudioCallbackCodec.Read(delivery.Body);
        if (callback is null)
        {
            Record(MediaProcessingOutcome.Quarantined, started);
            logger.LogWarning("Media transcoding result delivery rejected. {ErrorCode}", "MEDIA_AUDIO_CALLBACK_REJECTED");
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<MediaAudioPreparationService>();
            var applied = await service.WakeAsync(callback, deadline.Token);
            await queue.AcknowledgeAsync(delivery.ReceiptHandle, deadline.Token);
            Record(applied ? MediaProcessingOutcome.Acknowledged : MediaProcessingOutcome.Ignored, started);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Record(MediaProcessingOutcome.Retry, started);
            logger.LogWarning("Media transcoding result exceeded its time budget. {ErrorCode}", "MEDIA_AUDIO_RESULT_TIMEOUT");
        }
        catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException
            or MediaPreparationWriteException or MediaProviderUnavailableException or MediaAudioResultQueueException)
        {
            Record(MediaProcessingOutcome.Retry, started);
            logger.LogWarning("Media transcoding result will be redelivered. {ErrorCode}", "MEDIA_AUDIO_RESULT_RETRY");
        }
    }

    private void Record(MediaProcessingOutcome outcome, long started) =>
        telemetry?.Record(MediaProcessingStage.ResultConsumption, outcome,
            durationSeconds: Stopwatch.GetElapsedTime(started).TotalSeconds);
}
