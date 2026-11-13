using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ScrapBook.UnitTests.Stubs;

internal sealed class HealthCheckServiceStub(HealthStatus status) : HealthCheckService
{
    public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["private-dependency"] = new(status, "private diagnostics", TimeSpan.Zero, null, null)
        };
        return Task.FromResult(new HealthReport(entries, TimeSpan.Zero));
    }
}
