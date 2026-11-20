using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ScrapBook.Api.Writers;

namespace ScrapBook.UnitTests.Writers;

public sealed class HealthHttpResponseWriterTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, 200, "up")]
    [InlineData(HealthStatus.Degraded, 503, "down")]
    [InlineData(HealthStatus.Unhealthy, 503, "down")]
    public async Task WritesSanitizedStatusAsync(HealthStatus status, int expectedStatus, string expectedBody)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["postgres"] = new(status, "internal-fixture", TimeSpan.Zero, null, null)
        };
        await HealthHttpResponseWriter.WriteAsync(context, new HealthReport(entries, status, TimeSpan.Zero), CancellationToken.None);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: CancellationToken.None);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(expectedBody, json.RootElement.GetProperty("status").GetString());
    }

}
