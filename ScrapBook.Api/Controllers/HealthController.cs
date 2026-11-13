using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ScrapBook.Api.Writers;

namespace ScrapBook.Api.Controllers;

[ApiController]
[Route("/health")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class HealthController(HealthCheckService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAsync()
    {
        var report = await service.CheckHealthAsync(HttpContext.RequestAborted);
        Response.StatusCode = report.Status == HealthStatus.Healthy ? 200 : 503;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "Thu, 01 Jan 1970 00:00:00 GMT";
        await HealthHttpResponseWriter.WriteAsync(HttpContext, report);
        return new EmptyResult();
    }
}
