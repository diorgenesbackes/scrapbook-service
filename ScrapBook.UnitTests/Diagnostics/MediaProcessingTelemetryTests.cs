using System.Diagnostics.Metrics;
using ScrapBook.Application.Diagnostics;

namespace ScrapBook.UnitTests.Diagnostics;

public sealed class MediaProcessingTelemetryTests
{
    [Fact]
    public void RecordsOnlyBoundedReadinessDimensionsInTheMeterAndInterval()
    {
        var testContext = new AsyncLocal<bool> { Value = true };
        var measurements = new List<(string? Unit, double Seconds, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "ScrapBook.MediaProcessing" && instrument.Name == "scrapbook.media.ready.duration")
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, seconds, tags, _) =>
        {
            if (testContext.Value) measurements.Add((instrument.Unit, seconds, tags.ToArray()));
        });
        listener.Start();
        var telemetry = new MediaProcessingTelemetry();

        telemetry.Record(MediaProcessingStage.Ready, MediaProcessingOutcome.Available, "video", false, 300);
        telemetry.Record(MediaProcessingStage.Ready, MediaProcessingOutcome.Available, "private-object-path", null, 301);

        Assert.Collection(measurements,
            entry => AssertReadinessDimension(entry, "video", "recent", 300),
            entry => AssertReadinessDimension(entry, "unknown", "unknown", 301));
        var intervals = telemetry.CollectInterval();
        Assert.Equal(2, intervals.Count);
        Assert.Contains(intervals, entry => entry is { MediaType: "video", Cohort: "recent", SampleCount: 1 });
        Assert.Contains(intervals, entry => entry is { MediaType: "unknown", Cohort: "unknown", SampleCount: 1 });
    }

    [Fact]
    public void KeepsOutcomeCountsAndCumulativeDurationBucketsAcrossAnInterval()
    {
        var telemetry = new MediaProcessingTelemetry();
        foreach (var duration in new double?[] { 0.5, 300, 300.25, 1801, null })
        {
            telemetry.Record(MediaProcessingStage.Ready, MediaProcessingOutcome.Available, "audio", false, duration);
        }

        var interval = Assert.Single(telemetry.CollectInterval());

        Assert.Equal(5, interval.SampleCount);
        Assert.Equal(4, interval.DurationSampleCount);
        Assert.Equal(2401.75, interval.DurationSumSeconds);
        Assert.Equal(1801, interval.DurationMaximumSeconds);
        Assert.Equal(2, interval.WithinTargetCount);
        Assert.Equal(1, interval.DurationBuckets["0.5"]);
        Assert.Equal(2, interval.DurationBuckets["300"]);
        Assert.Equal(3, interval.DurationBuckets["1800"]);
        Assert.Equal(4, interval.DurationBuckets["+Inf"]);
        Assert.Empty(telemetry.CollectInterval());
    }

    private static void AssertReadinessDimension((string? Unit, double Seconds, KeyValuePair<string, object?>[] Tags) entry,
        string mediaType, string cohort, double seconds)
    {
        Assert.Equal(("s", seconds), (entry.Unit, entry.Seconds));
        var tags = entry.Tags.ToDictionary();
        Assert.Equal(4, tags.Count);
        Assert.Equal("ready", tags["stage"]);
        Assert.Equal(mediaType, tags["media_type"]);
        Assert.Equal(cohort, tags["cohort"]);
        Assert.Equal("available", tags["outcome"]);
    }
}
