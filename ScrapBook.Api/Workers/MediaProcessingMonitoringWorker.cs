using ScrapBook.Application.Diagnostics;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Workers;

public sealed class MediaProcessingMonitoringWorker(IServiceScopeFactory scopes, TimeProvider clock,
    MediaProcessingTelemetry telemetry, ILogger<MediaProcessingMonitoringWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SampleInterval, clock);
        do
        {
            foreach (var interval in telemetry.CollectInterval())
            {
                LogInterval(interval);
            }
            await ReadBacklogAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void LogInterval(MediaProcessingTelemetrySnapshot interval) =>
        logger.LogInformation(new EventId(4301, "media.processing.interval"),
            "Media processing interval. {Stage} {MediaType} {Cohort} {Outcome} {SampleCount} {DurationSampleCount} "
            + "{DurationSumSeconds} {DurationMaximumSeconds} {WithinTargetCount} {DurationBuckets} {FailureStage}",
            interval.Stage, interval.MediaType, interval.Cohort, interval.Outcome, interval.SampleCount,
            interval.DurationSampleCount, interval.DurationSumSeconds, interval.DurationMaximumSeconds,
            interval.WithinTargetCount, interval.DurationBuckets, interval.FailureStage);

    private async Task ReadBacklogAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<MediaProcessingMonitoringService>();
            var sampledAt = clock.GetUtcNow();
            RecordBacklog(await service.ReadBacklogAsync(deadline.Token), sampledAt);
            var jobs = await service.ReadProcessingBacklogAsync(sampledAt, deadline.Token);
            foreach (var job in jobs) RecordJobBacklog(job, sampledAt);
        }
        catch (Exception error) when (error is PostgresUnavailableException or PostgresWriteRejectedException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            logger.LogWarning(new EventId(4303, "media.processing.backlog_sample_unavailable"),
                "Media backlog sample was unavailable. {ErrorCode}", "MEDIA_BACKLOG_SAMPLE_UNAVAILABLE");
        }
    }

    private void RecordBacklog(OutboxBacklogModel backlog, DateTimeOffset sampledAt)
    {
        var oldest = GetAgeSeconds(backlog.OldestPendingAt, sampledAt);
        telemetry.RecordBacklog(backlog, oldest);
        logger.LogInformation(new EventId(4302, "media.processing.outbox_sample"),
            "Media confirmation Outbox sample. {PendingCount} {FailedCount} {OldestPendingAgeSeconds}",
            backlog.PendingCount, backlog.FailedCount, oldest);
    }

    private void RecordJobBacklog(MediaProcessingBacklogModel backlog, DateTimeOffset sampledAt)
    {
        var oldest = GetAgeSeconds(backlog.OldestPendingAt, sampledAt);
        telemetry.RecordJobBacklog(backlog, oldest);
        logger.LogInformation(new EventId(4304, "media.processing.jobs_sample"),
            "Media jobs sample. {MediaType} {Cohort} {PendingCount} {ActiveCount} {ReconcilingCount} {OldestPendingAgeSeconds}",
            backlog.MediaType, backlog.IsHistorical ? "historical" : "recent", backlog.PendingCount,
            backlog.ActiveCount, backlog.ReconcilingCount, oldest);
    }

    private static double GetAgeSeconds(DateTimeOffset? oldest, DateTimeOffset sampledAt) =>
        oldest is { } time ? Math.Max(0, (sampledAt - time).TotalSeconds) : 0;
}
