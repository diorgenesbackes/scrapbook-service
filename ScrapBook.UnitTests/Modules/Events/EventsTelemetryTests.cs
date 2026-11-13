using System.Diagnostics;
using System.Diagnostics.Metrics;
using ScrapBook.Application.Modules.Events;

namespace ScrapBook.UnitTests.Modules.Events;

public sealed class EventsTelemetryTests
{
    [Fact]
    public void MetricsHaveOnlyAggregatedDimensionsAndDependenciesHaveRealSpans()
    {
        var measurements = new List<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == EventsTelemetry.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();
        using var telemetry = new EventsTelemetry("Development", "synthetic", 1);
        using (var request = telemetry.Source.StartActivity("events.create"))
        {
            Assert.NotNull(request);
            Assert.Equal(ActivityIdFormat.W3C, request.IdFormat);
            using var dependency = telemetry.Dependency("postgresql", "events.sql.create");
            Assert.Equal(request.TraceId, Activity.Current?.TraceId);
            Assert.Equal(request.SpanId, Activity.Current?.ParentSpanId);
            dependency.Complete();
        }
        telemetry.Record("events.update_structure", "PUT", 409, "rejected", 4.5, EventFailureKind.VersionConflict, false);
        telemetry.Record("events.update_access", "PUT", 409, "rejected", 4.5, EventFailureKind.OpeningLocked, false);
        telemetry.Record("events.list", "GET", 503, "failure", 4.5, EventFailureKind.DependencyUnavailable, true);
        telemetry.Record("events.list", "GET", null, "client_aborted", 4.5, null, false);
        Assert.Contains(measurements, m => m.Name == "scrapbook.events.requests");
        Assert.Contains(measurements, m => m.Name == "scrapbook.events.duration_ms");
        Assert.Contains(measurements, m => m.Name == "scrapbook.events.version_conflicts");
        Assert.Contains(measurements, m => m.Name == "scrapbook.events.opening_conflicts");
        Assert.Contains(measurements, m => m.Name == "scrapbook.events.persistence_failures");
        Assert.Contains(measurements, m => m.Name == "scrapbook.events.dependency.duration_ms");
        string[] allowed = ["operation", "method", "status", "outcome", "environment", "dependency"];
        Assert.All(measurements, measurement => Assert.All(measurement.Tags, tag => Assert.Contains(tag.Key, allowed)));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void InvalidSamplingRateIsRejected(double rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventsTelemetry("Development", "synthetic", rate));
}
