using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Diagnostics;

public sealed class MediaProcessingTelemetry
{
    internal const double ReadinessTargetSeconds = 300;
    private static readonly ActivitySource Trace = new("ScrapBook.MediaProcessing");
    private static readonly Meter Meter = new("ScrapBook.MediaProcessing");
    private static readonly Histogram<double> Readiness = Meter.CreateHistogram<double>("scrapbook.media.ready.duration", "s",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = MediaProcessingTelemetryInterval.BucketLimitsSeconds });
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("scrapbook.media.processing.duration", "s",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = MediaProcessingTelemetryInterval.BucketLimitsSeconds });
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("scrapbook.media.processing.outcomes", "{outcome}");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("scrapbook.media.processing.failures", "{failure}");
    private static readonly Gauge<long> Pending = Meter.CreateGauge<long>("scrapbook.media.outbox.pending", "{message}");
    private static readonly Gauge<long> Failed = Meter.CreateGauge<long>("scrapbook.media.outbox.failed", "{message}");
    private static readonly Gauge<double> Oldest = Meter.CreateGauge<double>("scrapbook.media.outbox.oldest.age", "s");

    private static readonly Gauge<long> JobsBacklog = Meter.CreateGauge<long>("scrapbook.media.jobs.backlog", "{job}");
    private static readonly Gauge<double> OldestJob = Meter.CreateGauge<double>("scrapbook.media.jobs.oldest.age", "s");

    private readonly Lock sync = new();
    private Dictionary<(string Stage, string MediaType, string Cohort, string Outcome, string? FailureStage),
        MediaProcessingTelemetryInterval> intervals = [];

    public static Activity? StartActivity(MediaProcessingStage stage, ActivityKind kind) =>
        Trace.StartActivity("media." + Name(stage), kind);

    internal void RecordFirstClaim(MediaJobEntity job, string kind, DateTimeOffset observedAt) =>
        Record(MediaProcessingStage.ClaimWait, MediaProcessingOutcome.Started, kind, job.IsHistorical,
            (observedAt - (job.AdmittedAt ?? job.ConfirmedAt)).TotalSeconds);

    internal void RecordReady(MediaJobEntity job, string kind, DateTimeOffset observedAt)
    {
        var baseline = job.IsHistorical ? job.AdmittedAt ?? job.ConfirmedAt : job.ConfirmedAt;
        Record(MediaProcessingStage.Ready, MediaProcessingOutcome.Available, kind, job.IsHistorical,
            (observedAt - baseline).TotalSeconds);
    }

    internal void RecordFailure(string kind, bool historical, string stage) =>
        Record(MediaProcessingStage.Failure, MediaProcessingOutcome.Failed, kind, historical, null,
            stage is "publish" or "prepare" or "verify" or "transcode" ? stage : "unknown");

    public void Record(MediaProcessingStage stage, MediaProcessingOutcome outcome, string? kind = null,
        bool? isHistorical = null, double? durationSeconds = null) =>
        Record(stage, outcome, kind, isHistorical, durationSeconds, null);

    private void Record(MediaProcessingStage stage, MediaProcessingOutcome outcome, string? kind, bool? isHistorical,
        double? durationSeconds, string? failureStage)
    {
        var stageName = Name(stage);
        var outcomeName = Name(outcome);
        var mediaType = kind is "photo" or "audio" or "video" ? kind : "unknown";
        var cohort = isHistorical switch { true => "historical", false => "recent", null => "unknown" };
        var duration = durationSeconds is { } seconds && double.IsFinite(seconds) ? Math.Max(0, seconds) : (double?)null;
        var key = (stageName, mediaType, cohort, outcomeName, failureStage);

        lock (sync)
        {
            if (!intervals.TryGetValue(key, out var interval))
            {
                interval = new();
                intervals.Add(key, interval);
            }
            interval.Add(duration, stage == MediaProcessingStage.Ready);
        }

        var tags = new TagList { { "stage", stageName }, { "media_type", mediaType }, { "cohort", cohort }, { "outcome", outcomeName } };
        Outcomes.Add(1, tags);
        if (failureStage is not null)
        {
            tags.Add("failure_stage", failureStage);
            Failures.Add(1, tags);
        }
        if (duration is { } elapsed)
        {
            (stage == MediaProcessingStage.Ready ? Readiness : Duration).Record(elapsed, tags);
        }
    }

    public void RecordBacklog(OutboxBacklogModel backlog, double oldestSeconds)
    {
        Pending.Record(backlog.PendingCount);
        Failed.Record(backlog.FailedCount);
        Oldest.Record(oldestSeconds);
    }

    public void RecordJobBacklog(MediaProcessingBacklogModel backlog, double oldestSeconds)
    {
        var tags = new TagList
        {
            { "media_type", backlog.MediaType },
            { "cohort", backlog.IsHistorical ? "historical" : "recent" }
        };
        OldestJob.Record(oldestSeconds, tags);
        RecordJobCount(backlog.PendingCount, "pending", tags);
        RecordJobCount(backlog.ActiveCount, "running", tags);
        RecordJobCount(backlog.ReconcilingCount, "reconciling", tags);
    }

    private static void RecordJobCount(long count, string state, TagList tags)
    {
        tags.Add("state", state);
        JobsBacklog.Record(count, tags);
    }

    public IReadOnlyList<MediaProcessingTelemetrySnapshot> CollectInterval()
    {
        Dictionary<(string Stage, string MediaType, string Cohort, string Outcome, string? FailureStage),
            MediaProcessingTelemetryInterval> collected;
        lock (sync)
        {
            collected = intervals;
            intervals = [];
        }
        return collected.Select(entry => entry.Value.Snapshot(entry.Key.Stage, entry.Key.MediaType, entry.Key.Cohort,
            entry.Key.Outcome, entry.Key.FailureStage)).ToArray();
    }

    private static string Name<T>(T value) where T : struct, Enum =>
        Enum.GetName(value) is { } name ? JsonNamingPolicy.SnakeCaseLower.ConvertName(name) : "unknown";
}
