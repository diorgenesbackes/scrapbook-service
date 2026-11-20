using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ScrapBook.Api.Middlewares;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Api.ExceptionHandlers;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        var unavailable = exception is PostgresUnavailableException;
        var status = unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
        var code = unavailable ? "DEPENDENCY_UNAVAILABLE" : "UNEXPECTED_ERROR";
        var correlation = CorrelationMiddleware.GetCorrelation(httpContext);
        logger.LogError("Falha HTTP {ErrorCode} com correlação {CorrelationId}", code, correlation);

        var details = new ProblemDetails
        {
            Type = "about:blank",
            Title = unavailable ? "Dependência indisponível" : "Falha interna",
            Status = status,
            Detail = "Ocorreu uma falha temporária."
        };
        details.Extensions["code"] = code;
        details.Extensions["correlationId"] = correlation;
        if (Activity.Current is { } activity && activity.TraceId != default)
        {
            details.Extensions["traceId"] = activity.TraceId.ToString();
        }

        httpContext.Response.StatusCode = status;
        httpContext.Response.Headers.CacheControl = "no-store";
        await httpContext.Response.WriteAsJsonAsync(details, options: null,
            contentType: "application/problem+json", cancellationToken);
        return true;
    }
}
