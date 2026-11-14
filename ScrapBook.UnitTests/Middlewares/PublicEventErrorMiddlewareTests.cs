using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Options;
using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Enums;
using ScrapBook.UnitTests.Stubs;

namespace ScrapBook.UnitTests.Middlewares;

public sealed class PublicEventErrorMiddlewareTests
{
    [Theory]
    [InlineData("missing", 404, "RESOURCE_NOT_FOUND")]
    [InlineData("timeout", 503, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("cancelled", 503, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("unexpected", 500, "UNEXPECTED_ERROR")]
    public async Task PublicFailuresHaveSafeProblemDetailsAndNominalRoute(string kind, int status, string code)
    {
        const string sensitive = "PRIVATE SQL password event-name public-code";
        var error = kind switch
        {
            "missing" => (Exception)new EventApplicationException(EventFailureKindEnum.NotFound),
            "timeout" => new TimeoutException(sensitive),
            "cancelled" => new OperationCanceledException(sensitive),
            _ => new InvalidOperationException(sensitive)
        };
        var logger = new RequestLoggerStub();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = Context(services);
        var supplied = Guid.NewGuid().ToString();
        context.Request.Headers["X-Correlation-Id"] = supplied;
        await new EventRequestMiddleware(_ => throw error, logger).InvokeAsync(context,
            new DevelopmentCurrentActorService(false, 0, new AccountRepositoryStub(false)), new DevelopmentIdentityOptions(), new EventTelemetryService("Testing", "test"));
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var body = JsonDocument.Parse(text);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(supplied, context.Response.Headers["X-Correlation-Id"].ToString());
        Assert.Equal(supplied, body.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain(sensitive, text);
        Assert.False(body.RootElement.TryGetProperty("exception", out _));
        Assert.False(body.RootElement.TryGetProperty("instance", out _));
        var log = Assert.Single(logger.Entries);
        Assert.Contains("events.public_get", log);
        Assert.Contains("/api/v1/public/events/{code}", log);
        Assert.DoesNotContain("synthetic-sensitive-code", log);
        Assert.DoesNotContain(sensitive, log);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222")]
    public async Task InvalidCorrelationIsReplaced(string supplied)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = Context(services);
        context.Request.Headers["X-Correlation-Id"] = supplied;
        await new EventRequestMiddleware(_ => throw new TimeoutException(), new RequestLoggerStub()).InvokeAsync(context,
            new DevelopmentCurrentActorService(false, 0, new AccountRepositoryStub(false)), new DevelopmentIdentityOptions(), new EventTelemetryService("Testing", "test"));
        var result = context.Response.Headers["X-Correlation-Id"].ToString();
        Assert.True(Guid.TryParseExact(result, "D", out _));
        Assert.NotEqual(supplied, result);
    }

    [Fact]
    public async Task ClientAbandonmentDoesNotWriteABusinessFailure()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var context = Context(services);
        context.RequestAborted = cancellation.Token;
        var logger = new RequestLoggerStub();
        await new EventRequestMiddleware(_ => { cancellation.Cancel(); throw new OperationCanceledException(); }, logger)
            .InvokeAsync(context, new DevelopmentCurrentActorService(false, 0, new AccountRepositoryStub(false)),
                new DevelopmentIdentityOptions(), new EventTelemetryService("Testing", "test"));
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Contains("client_aborted", Assert.Single(logger.Entries));
    }

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/v1/public/events/synthetic-sensitive-code";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/v1/public/events/{code}"), 0,
            new EndpointMetadataCollection(new EndpointNameMetadata("GetPublicEvent")), "GetPublicEvent"));
        return context;
    }
}
