using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ScrapBook.Api.ExceptionHandlers;
using ScrapBook.Api.Middlewares;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.UnitTests.ExceptionHandlers;

public sealed class GlobalExceptionHandlerTests
{
    [Theory]
    [InlineData(true, 503, "DEPENDENCY_UNAVAILABLE")]
    [InlineData(false, 500, "UNEXPECTED_ERROR")]
    public async Task WritesSafeProblemDetailsAsync(bool databaseFailure, int status, string code)
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var correlation = Guid.NewGuid();
        context.Items[CorrelationMiddleware.HeaderName] = correlation;
        using var body = new MemoryStream();
        context.Response.Body = body;
        Exception exception = databaseFailure ? new PostgresUnavailableException() : new InvalidOperationException("fixture-secret");
        Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));
        var payload = Encoding.UTF8.GetString(body.ToArray());
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(correlation, json.RootElement.GetProperty("correlationId").GetGuid());
        Assert.Equal(activity.TraceId.ToString(), json.RootElement.GetProperty("traceId").GetString());
        Assert.DoesNotContain("fixture-secret", payload);
    }

    [Fact]
    public async Task WritesPublicNotFoundWithNoCacheAsync()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/public-events/fixture-secret";
        using var body = new MemoryStream();
        context.Response.Body = body;

        Assert.True(await handler.TryHandleAsync(context, new PublicEventNotFoundException(), CancellationToken.None));

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        using var json = JsonDocument.Parse(body.ToArray());
        Assert.Equal("Evento indisponível.", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("RESOURCE_NOT_FOUND", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("fixture-secret", json.RootElement.GetRawText());
    }

    [Fact]
    public async Task PreservesClientCancellationAsync()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        context.RequestAborted = canceled.Token;
        using var body = new MemoryStream();
        context.Response.Body = body;
        Assert.True(await handler.TryHandleAsync(context, new OperationCanceledException(canceled.Token), canceled.Token));
        Assert.Empty(body.ToArray());
    }
}
