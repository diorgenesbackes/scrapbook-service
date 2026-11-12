using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ScrapBook.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointConventionBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapHealthChecks("/health", new HealthCheckOptions
        {
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            },
            ResponseWriter = HealthResponseWriter.WriteAsync
        });
    }
}
