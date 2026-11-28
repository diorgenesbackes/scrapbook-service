using System.Globalization;

namespace ScrapBook.Application.Diagnostics;

internal sealed class MediaProcessingTelemetryInterval
{
    internal static IReadOnlyList<double> BucketLimitsSeconds { get; } = [0.5, 2, 5, 15, 30, 60, 120, 300, 600, 1800];
    private readonly long[] buckets = new long[BucketLimitsSeconds.Count];
    private long sampleCount;
    private long durationSampleCount;
    private double durationSumSeconds;
    private double durationMaximumSeconds;
    private long withinTargetCount;

    public void Add(double? durationSeconds, bool readiness)
    {
        sampleCount++;
        if (durationSeconds is not { } duration) return;

        durationSampleCount++;
        durationSumSeconds += duration;
        durationMaximumSeconds = Math.Max(durationMaximumSeconds, duration);
        if (readiness && duration <= MediaProcessingTelemetry.ReadinessTargetSeconds) withinTargetCount++;

        for (var index = 0; index < BucketLimitsSeconds.Count; index++)
        {
            if (duration > BucketLimitsSeconds[index]) continue;
            buckets[index]++;
            return;
        }
    }

    public MediaProcessingTelemetrySnapshot Snapshot(string stage, string mediaType, string cohort, string outcome,
        string? failureStage)
    {
        var cumulativeBuckets = new Dictionary<string, long>();
        var cumulativeCount = 0L;
        for (var index = 0; index < BucketLimitsSeconds.Count; index++)
        {
            cumulativeCount += buckets[index];
            cumulativeBuckets.Add(BucketLimitsSeconds[index].ToString(CultureInfo.InvariantCulture), cumulativeCount);
        }
        cumulativeBuckets.Add("+Inf", durationSampleCount);
        return new(stage, mediaType, cohort, outcome, sampleCount, durationSampleCount, durationSumSeconds,
            durationMaximumSeconds, withinTargetCount, cumulativeBuckets, failureStage);
    }
}
