using ScrapBook.Application.Enums;
using ScrapBook.Application.Scopes;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace ScrapBook.Application.Services;

public sealed class EventTelemetryService : IDisposable
{
    public const string SourceName = "ScrapBook.Events";
    public const string MeterName = "ScrapBook.Events";
    public string EnvironmentName { get; }
    public string Version { get; }
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public ActivitySource Source { get; }
    private readonly Meter meter;
    private readonly ActivityListener listener;
    private readonly Counter<long> requests, versionConflicts, openingConflicts, persistenceFailures;
    private readonly Histogram<double> duration, dependencyDuration;

    public EventTelemetryService(string environmentName, string version, double samplingRate = 0)
    {
        if (!double.IsFinite(samplingRate) || samplingRate is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(samplingRate));
        EnvironmentName = environmentName; Version = version;
        Source = new ActivitySource(SourceName, version);
        meter = new Meter(MeterName, version);
        requests = meter.CreateCounter<long>("scrapbook.events.requests", "{request}");
        duration = meter.CreateHistogram<double>("scrapbook.events.duration_ms", "ms");
        versionConflicts = meter.CreateCounter<long>("scrapbook.events.version_conflicts");
        openingConflicts = meter.CreateCounter<long>("scrapbook.events.opening_conflicts");
        persistenceFailures = meter.CreateCounter<long>("scrapbook.events.persistence_failures");
        dependencyDuration = meter.CreateHistogram<double>("scrapbook.events.dependency.duration_ms", "ms");
        listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => Random.Shared.NextDouble() < samplingRate
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(listener);
    }

    public void Record(string operation, string method, int? status, string outcome, double milliseconds,
        EventFailureKindEnum? failure, bool persistenceFailure)
    {
        var tags = new TagList { { "operation", operation }, { "method", method }, { "status", status?.ToString(CultureInfo.InvariantCulture) ?? "none" },
            { "outcome", outcome }, { "environment", EnvironmentName } };
        requests.Add(1, tags); duration.Record(milliseconds, tags);
        if (failure == EventFailureKindEnum.VersionConflict) versionConflicts.Add(1, tags);
        if (failure == EventFailureKindEnum.OpeningLocked) openingConflicts.Add(1, tags);
        if (persistenceFailure) persistenceFailures.Add(1, tags);
    }

    public DependencyScope Dependency(string kind, string operation) => new(this, kind, operation);
    internal void RecordDependency(string kind, string operation, bool completed, double milliseconds) =>
        dependencyDuration.Record(milliseconds, new TagList { { "dependency", kind }, { "operation", operation },
            { "outcome", completed ? "success" : "error" }, { "environment", EnvironmentName } });
    public void Dispose() { listener.Dispose(); Source.Dispose(); meter.Dispose(); }
}
