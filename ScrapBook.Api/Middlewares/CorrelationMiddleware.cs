using System.Diagnostics;

namespace ScrapBook.Api.Middlewares;

public sealed partial class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
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
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        var method = context.Request.Method switch
        {
            "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS" => context.Request.Method,
            _ => "OTHER"
        };
        var activity = Activity.Current;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlationId"] = correlation,
            ["traceId"] = activity?.TraceId.ToString(),
            ["spanId"] = activity?.SpanId.ToString(),
            ["requestId"] = context.TraceIdentifier,
            ["route"] = route,
            ["operation"] = route,
            ["httpMethod"] = method
        });

        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (context.RequestAborted.IsCancellationRequested)
            {
                LogAborted(logger, elapsed, "client_aborted");
            }
            else
            {
                var status = context.Response.StatusCode;
                var outcome = status switch
                {
                    >= 500 => "failure",
                    >= 400 => "rejected",
                    _ => "success"
                };
                LogCompleted(logger, status, elapsed, outcome);
            }
        }
    }

    public static Guid GetCorrelation(HttpContext context) =>
        context.Items.TryGetValue(HeaderName, out var value) && value is Guid correlation ? correlation : Guid.NewGuid();

    [LoggerMessage(EventId = 1000, EventName = "http.request.completed", Level = LogLevel.Information,
        Message = "Requisição concluída com {StatusCode} em {DurationMs} ms ({Outcome})")]
    private static partial void LogCompleted(ILogger logger, int statusCode, double durationMs, string outcome);

    [LoggerMessage(EventId = 1001, EventName = "http.request.aborted", Level = LogLevel.Information,
        Message = "Requisição cancelada pelo cliente em {DurationMs} ms ({Outcome})")]
    private static partial void LogAborted(ILogger logger, double durationMs, string outcome);
}
