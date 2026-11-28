using System.Diagnostics;
using ScrapBook.Application.Diagnostics;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Workers;

public sealed class MediaAdmissionWorker(IMediaConfirmationQueue queue, IServiceScopeFactory scopes, ILogger<MediaAdmissionWorker> logger,
    MediaProcessingTelemetry? telemetry = null)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            IReadOnlyList<MediaConfirmationDelivery> batch;
            try
            {
                batch = await queue.ReceiveAsync(4, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Media confirmation queue polling exceeded its time budget. {ErrorCode}", "MEDIA_QUEUE_TIMEOUT");
                continue;
            }
            catch (MediaConfirmationQueueException)
            {
                logger.LogWarning("Media confirmation queue polling unavailable. {ErrorCode}", "MEDIA_QUEUE_UNAVAILABLE");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            await Task.WhenAll(batch.Select(delivery => AdmitAsync(delivery, stoppingToken)));
        }
    }

    private async Task AdmitAsync(MediaConfirmationDelivery delivery, CancellationToken cancellationToken)
    {
        using var activity = MediaProcessingTelemetry.StartActivity(MediaProcessingStage.ConfirmationConsumption, ActivityKind.Consumer);
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<MediaAdmissionService>();
            var result = await service.AdmitAsync(delivery.Body, deadline.Token);
            if (result is MediaAdmissionResult.Admitted or MediaAdmissionResult.Duplicate)
            {
                await queue.AcknowledgeAsync(delivery.ReceiptHandle, deadline.Token);
            }
            else
            {
                logger.LogWarning("Media confirmation delivery rejected. {ErrorCode}", "MEDIA_CONFIRMATION_REJECTED");
            }
            Record(result switch
            {
                MediaAdmissionResult.Admitted => MediaProcessingOutcome.Admitted,
                MediaAdmissionResult.Duplicate => MediaProcessingOutcome.Duplicate,
                _ => MediaProcessingOutcome.Quarantined
            }, started);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Record(MediaProcessingOutcome.Retry, started);
            logger.LogWarning("Media confirmation exceeded its time budget. {ErrorCode}", "MEDIA_CONFIRMATION_TIMEOUT");
        }
        catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException or MediaConfirmationQueueException)
        {
            Record(MediaProcessingOutcome.Retry, started);
            logger.LogWarning("Media confirmation will be redelivered. {ErrorCode}", "MEDIA_CONFIRMATION_RETRY");
        }
    }

    private void Record(MediaProcessingOutcome outcome, long started) =>
        telemetry?.Record(MediaProcessingStage.ConfirmationConsumption, outcome,
            durationSeconds: Stopwatch.GetElapsedTime(started).TotalSeconds);
}
