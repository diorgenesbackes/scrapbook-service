using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ScrapBook.Api.Endpoints;

namespace ScrapBook.UnitTests.Endpoints;

public sealed class HealthResponseWriterTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, "up")]
    [InlineData(HealthStatus.Degraded, "down")]
    [InlineData(HealthStatus.Unhealthy, "down")]
    public async Task WritesOnlyPublicStatusWithoutDependencyDetails(
        HealthStatus healthStatus,
        string expectedStatus)
    {
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["private-dependency"] = new(
                    healthStatus,
                    "sensitive-description",
                    TimeSpan.Zero,
                    new InvalidOperationException("sensitive-exception"),
                    new Dictionary<string, object> { ["token"] = "sensitive-value" })
            },
            TimeSpan.Zero);

        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await HealthResponseWriter.WriteAsync(context, report);

        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("status").GetString());
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
    }
}
