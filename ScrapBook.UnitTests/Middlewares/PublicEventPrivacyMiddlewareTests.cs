using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Options;
using ScrapBook.Application.Services;
using ScrapBook.UnitTests.Stubs;

namespace ScrapBook.UnitTests.Middlewares;

public sealed class PublicEventPrivacyMiddlewareTests
{
    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("PRIVATE_GUEST_MARKER", "OTHER")]
    public async Task HeadersAndSignalsNeverCarryRequestData(string method, string expectedMethod)
    {
        const string marker = "PRIVATE_GUEST_MARKER";
        using var telemetry = new EventTelemetryService("Testing", "privacy", 1);
        var spans = new ConcurrentBag<Activity>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(activities);
        var measurements = new ConcurrentBag<KeyValuePair<string, object?>[]>();
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == EventTelemetryService.MeterName) listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        metrics.SetMeasurementEventCallback<double>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        metrics.Start();
        var logger = new RequestLoggerStub();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/public/events/" + marker;
        context.Request.QueryString = new QueryString("?sender=" + marker);
        context.Request.Headers.Referer = "https://private.invalid/" + marker;
        context.Request.Headers.Authorization = "Bearer " + marker;
        context.Request.Method = method;
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/v1/public/events/{code}"), 0,
            new EndpointMetadataCollection(new EndpointNameMetadata("GetPublicEvent")), "GetPublicEvent"));
        await new EventRequestMiddleware(_ => Task.CompletedTask, logger).InvokeAsync(context,
            new DevelopmentCurrentActorService(false, 0, new AccountRepositoryStub(false)), new DevelopmentIdentityOptions(), telemetry);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        Assert.DoesNotContain(marker, Assert.Single(logger.Entries));
        Assert.Contains(measurements, tags => tags.Any(tag => tag.Key == "method" && Equals(tag.Value, expectedMethod)));
        Assert.All(measurements, tags => Assert.All(tags, tag =>
        {
            Assert.Contains(tag.Key, new[] { "operation", "method", "status", "outcome", "environment", "dependency" });
            Assert.DoesNotContain(marker, tag.Value?.ToString() ?? "");
        }));
        var span = Assert.Single(spans);
        Assert.Equal("events.public_get", span.OperationName);
        Assert.Equal("/api/v1/public/events/{code}", span.GetTagItem("http.route"));
        Assert.All(span.TagObjects, tag => Assert.DoesNotContain(marker, tag.Value?.ToString() ?? ""));
        Assert.Empty(span.Events);
    }
}
