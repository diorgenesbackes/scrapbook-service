using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ScrapBook.Api.Controllers;
using ScrapBook.UnitTests.Stubs;

namespace ScrapBook.UnitTests.Controllers;

public sealed class HealthControllerTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, 200, "up")]
    [InlineData(HealthStatus.Degraded, 503, "down")]
    [InlineData(HealthStatus.Unhealthy, 503, "down")]
    public async Task PreservesStatusCacheAndMinimalResponse(HealthStatus status, int expectedHttp, string expectedStatus)
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var controller = new HealthController(new HealthCheckServiceStub(status))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        Assert.IsType<EmptyResult>(await controller.GetAsync());
        Assert.Equal(expectedHttp, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("status").GetString());
    }
}
