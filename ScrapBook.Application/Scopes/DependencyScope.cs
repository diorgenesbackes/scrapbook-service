using ScrapBook.Application.Services;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace ScrapBook.Application.Scopes;

public sealed class DependencyScope : IDisposable
{
    private readonly EventTelemetryService owner;
    private readonly string kind, operation;
    private readonly long start = Stopwatch.GetTimestamp();
    private readonly Activity? activity;
    private bool completed;
    internal DependencyScope(EventTelemetryService owner, string kind, string operation)
    {
        this.owner = owner; this.kind = kind; this.operation = operation;
        activity = owner.Source.StartActivity(operation, ActivityKind.Client);
        activity?.SetTag("module", "events"); activity?.SetTag("dependency", kind);
    }
    public void Complete() { completed = true; activity?.SetStatus(ActivityStatusCode.Ok); }
    public void Dispose()
    {
        if (!completed) activity?.SetStatus(ActivityStatusCode.Error);
        owner.RecordDependency(kind, operation, completed, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        activity?.Dispose();
    }
}
