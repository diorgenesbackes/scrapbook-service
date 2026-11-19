using System.Diagnostics;

namespace ScrapBook.Api.Middlewares;

public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[HeaderName];
        var correlation = values.Count == 1 && values[0] is { Length: 36 } header
            && Guid.TryParseExact(header, "D", out var parsed) ? parsed : Guid.NewGuid();
        context.Items[HeaderName] = correlation;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlation.ToString("D");
            return Task.CompletedTask;
        });

        var started = Stopwatch.GetTimestamp();
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlation,
            ["TraceId"] = Activity.Current?.TraceId.ToString()
        });

        try
        {
            await next(context);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (context.RequestAborted.IsCancellationRequested)
            {
                logger.LogInformation("Operação {Route} cancelada pelo cliente em {DurationMs} ms", route, elapsed);
            }
            else
            {
                logger.LogInformation("Operação {Route} retornou {StatusCode} em {DurationMs} ms",
                    route, context.Response.StatusCode, elapsed);
            }
        }
    }

    public static Guid GetCorrelation(HttpContext context) =>
        context.Items.TryGetValue(HeaderName, out var value) && value is Guid correlation ? correlation : Guid.NewGuid();
}
