using ScrapBook.Application.Diagnostics;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Api.Workers;

public sealed class OutboxMonitoringWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<OutboxMonitoringWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HistoricalSampleInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextHistoricalSample = DateTimeOffset.MinValue;
        var unmatchedHistory = false;

        using var timer = new PeriodicTimer(SampleInterval, clock);
        do
        {
            LogProcessingInterval();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<EventStructuralLockMonitoringService>();
                var now = clock.GetUtcNow();
                await RecordBacklogAsync(service, now, deadline.Token);
                if (now >= nextHistoricalSample)
                {
                    nextHistoricalSample = now + HistoricalSampleInterval;
                    var found = await service.HasUnmatchedConfirmationsAsync(deadline.Token);
                    if (found && !unmatchedHistory)
                    {
                        logger.LogWarning(new EventId(4202, "event.structural_lock.unmatched_history"),
                            "There are confirmed events awaiting source reconciliation. {ErrorCode}", "LOCK_HISTORICAL_FACT_MISSING");
                    }
                    unmatchedHistory = found;
                }
            }
            catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException
                || error is OperationCanceledException && !stoppingToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                logger.LogWarning("Structural lock Outbox sample was unavailable. {ErrorCode}", "OUTBOX_SAMPLE_UNAVAILABLE");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void LogProcessingInterval()
    {
        var interval = EventStructuralLockTelemetry.CollectInterval();
        logger.LogInformation(new EventId(4203, "event.structural_lock.processing_interval"),
            "Structural lock processing interval. Publications {PublicationOutcomes}, consumptions {ConsumptionOutcomes}, "
            + "redeliveries {Redeliveries}, lock samples {LockDurationSamples}, average seconds {AverageLockDurationSeconds}, "
            + "maximum seconds {MaximumLockDurationSeconds}, over target {LockDurationOverTargetCount}",
            interval.PublicationOutcomes, interval.ConsumptionOutcomes, interval.Redeliveries,
            interval.LockDurationSamples, interval.AverageLockDurationSeconds, interval.MaximumLockDurationSeconds,
            interval.LockDurationOverTargetCount);
    }

    private async Task RecordBacklogAsync(EventStructuralLockMonitoringService service, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var backlog = await service.ReadBacklogAsync(cancellationToken);
        var age = backlog.OldestPendingAt is { } oldest ? Math.Max(0, (now - oldest).TotalSeconds) : 0;
        EventStructuralLockTelemetry.RecordBacklog(backlog.PendingCount, backlog.FailedCount, age);
        logger.LogInformation(new EventId(4201, "event.structural_lock.outbox_sample"),
            "Structural lock Outbox sample. {PendingCount} {FailedCount} {OldestPendingAgeSeconds}",
            backlog.PendingCount, backlog.FailedCount, age);
    }
}
