using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ScrapBook.Api.Endpoints;

public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var status = report.Status == HealthStatus.Healthy ? "up" : "down";

        return context.Response.WriteAsJsonAsync(
            new { status },
            cancellationToken: context.RequestAborted);
    }
}
