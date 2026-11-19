using Microsoft.Extensions.Diagnostics.HealthChecks;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Api.Writers;

public static class HealthHttpResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report, CancellationToken cancellationToken)
    {
        var up = report.Status == HealthStatus.Healthy;
        context.Response.StatusCode = up ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new HealthResponse(up ? "up" : "down"), cancellationToken);
    }
}
