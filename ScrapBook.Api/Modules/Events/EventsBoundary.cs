using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ScrapBook.Api.Modules.Identity;
using ScrapBook.Application.Modules.Events;
using ScrapBook.Application.Modules.Identity;
using ScrapBook.Domain.Modules.Events;
using ScrapBook.Infrastructure.Modules.Events.Persistence;

namespace ScrapBook.Api.Modules.Events;

public sealed class EventsBoundary(RequestDelegate next, ILogger<EventsBoundary> logger)
{
    public const string ActorKey = "ScrapBook.Events.Actor";
    public const string CorrelationKey = "ScrapBook.Events.Correlation";
    public async Task InvokeAsync(HttpContext context, ICurrentActor currentActor, DevelopmentIdentityOptions identity, EventsTelemetry telemetry)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/events") || context.Request.Method == "OPTIONS")
        {
            await next(context); return;
        }
        var header = context.Request.Headers["X-Correlation-Id"];
        var correlation = header.Count == 1 && Guid.TryParseExact(header[0], "D", out var supplied) ? supplied : Guid.NewGuid();
        context.Items[CorrelationKey] = correlation;
        context.Response.Headers["X-Correlation-Id"] = correlation.ToString();
        context.Response.Headers.CacheControl = "no-store";
        var operation = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName switch
        {
            "CreateEvent" => "events.create", "ListEvents" => "events.list", "GetEvent" => "events.get",
            "UpdateEvent" => "events.update_structure", "UpdateEventAccess" => "events.update_access", _ => "events.unknown"
        };
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "/api/v1/events/*";
        var start = Stopwatch.GetTimestamp();
        EventFailureKind? failureKind = null;
        var persistenceFailure = false;
        string? exceptionType = null;
        using var activity = telemetry.Source.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("module", "events"); activity?.SetTag("http.route", route);
        var clientToken = context.RequestAborted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(clientToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        context.RequestAborted = budget.Token;
        try
        {
            DevelopmentOriginBoundary.Validate(context, identity);
            using (var identityDependency = telemetry.Dependency("identity", "identity.resolve"))
            {
                context.Items[ActorKey] = await currentActor.ResolveAsync(budget.Token)
                    ?? throw new EventFailure(EventFailureKind.AuthenticationRequired);
                identityDependency.Complete();
            }
            if (context.Request.Method is "POST" or "PUT") EventHttpContracts.ValidateBodyHeaders(context);
            await next(context);
        }
        catch (Exception error)
        {
            exceptionType = error.GetType().Name;
            context.RequestAborted = clientToken;
            if (clientToken.IsCancellationRequested || context.Response.HasStarted) return;
            var failure = Sanitize(error);
            failureKind = failure.Kind;
            persistenceFailure = error is DbUpdateException or NpgsqlException or TimeoutException
                || failure.Kind == EventFailureKind.DependencyUnavailable;
            await WriteProblemAsync(context, failure, correlation, clientToken);
        }
        finally
        {
            context.RequestAborted = clientToken;
            int? status = clientToken.IsCancellationRequested ? null : context.Response.StatusCode;
            var outcome = clientToken.IsCancellationRequested ? "client_aborted" : budget.IsCancellationRequested ? "timeout"
                : status < 400 ? "success" : status < 500 ? "rejected" : "failure";
            var durationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            activity?.SetTag("http.response.status_code", status);
            activity?.SetStatus(status >= 500 ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            telemetry.Record(operation, context.Request.Method, status, outcome, durationMs, failureKind, persistenceFailure);
            var eventId = failureKind == EventFailureKind.Unexpected ? new EventId(4003, "EventsUnexpectedFailure")
                : status >= 400 ? new EventId(4002, "EventsRequestRejected") : new EventId(4000, "EventsRequestCompleted");
            logger.Log(status >= 500 ? LogLevel.Error : status >= 400 ? LogLevel.Warning : LogLevel.Information, eventId,
                "{EventName} {ServiceName} {Component} {Module} {Operation} {HttpMethod} {RouteTemplate} {Status} {Outcome} {DurationMs} {CorrelationId} {TraceId} {SpanId} {RequestId} {ServiceVersion} {Environment} {InstanceId} {ErrorCode} {ExceptionType}",
                eventId.Name, "scrapbook-backend", "api", "events", operation, context.Request.Method, route, status, outcome, durationMs, correlation,
                Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.TraceId.ToHexString() : null,
                Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.SpanId.ToHexString() : null,
                context.TraceIdentifier, telemetry.Version, telemetry.EnvironmentName, telemetry.InstanceId, failureKind?.ToString(), exceptionType);
        }
    }
    public static EventFailure Sanitize(Exception error) => error switch
    {
        EventFailure known => known,
        EventRuleException rule => new(rule.Failure switch
        {
            EventRuleFailure.StructureLocked => EventFailureKind.StructureLocked,
            EventRuleFailure.OpeningLocked => EventFailureKind.OpeningLocked,
            _ => EventFailureKind.Validation
        }, rule.Field is null ? null : new Dictionary<string, string[]> { [rule.Field] = [rule.Message] }),
        BadHttpRequestException { StatusCode: 413 } => new(EventFailureKind.TooLarge),
        BadHttpRequestException or JsonException => new(EventFailureKind.Validation),
        DbUpdateException or NpgsqlException or TimeoutException or OperationCanceledException => EventPersistenceFailures.Translate(error),
        InvalidOperationException { InnerException: NpgsqlException } => EventPersistenceFailures.Translate(error),
        _ => new(EventFailureKind.Unexpected)
    };
    private static async Task WriteProblemAsync(HttpContext context, EventFailure failure, Guid correlation, CancellationToken token)
    {
        var (status, title, code, detail) = failure.Kind switch
        {
            EventFailureKind.Validation => (400, "Bad Request", "REQUEST_VALIDATION_FAILED", "Revise os dados informados."),
            EventFailureKind.AuthenticationRequired => (401, "Unauthorized", "IDENTITY_AUTHENTICATION_REQUIRED", "A identidade local de desenvolvimento não está configurada ou não é válida."),
            EventFailureKind.OriginNotAllowed => (403, "Forbidden", "REQUEST_ORIGIN_NOT_ALLOWED", "Origem da requisição não permitida."),
            EventFailureKind.NotFound => (404, "Not Found", "RESOURCE_NOT_FOUND", "Evento não encontrado."),
            EventFailureKind.StructureLocked => (409, "Conflict", "EVENT_LOCKED", "As informações deste evento não estão disponíveis para edição."),
            EventFailureKind.OpeningLocked => (409, "Conflict", "EVENT_OPENING_LOCKED", "A configuração de abertura não está disponível para alteração."),
            EventFailureKind.VersionConflict => (409, "Conflict", "EVENT_VERSION_CONFLICT", "O evento foi alterado. Recarregue os dados antes de salvar novamente."),
            EventFailureKind.TooLarge => (413, "Content Too Large", "REQUEST_TOO_LARGE", "A requisição excede o limite de 32 KiB."),
            EventFailureKind.UnsupportedMediaType => (415, "Unsupported Media Type", "UNSUPPORTED_MEDIA_TYPE", "Envie JSON UTF-8 com Content-Type application/json."),
            EventFailureKind.DependencyUnavailable => (503, "Service Unavailable", "DEPENDENCY_UNAVAILABLE", "Não foi possível confirmar a operação. Consulte o estado antes de repetir uma gravação."),
            _ => (500, "Internal Server Error", "UNEXPECTED_ERROR", "Não foi possível concluir a operação. Se estava salvando, confira o estado antes de repetir.")
        };
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Type = "about:blank", Title = title, Status = status, Detail = detail };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = correlation.ToString();
        if (failure.Errors is not null) problem.Extensions["errors"] = failure.Errors;
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity && activity.TraceId != default)
            problem.Extensions["traceId"] = activity.TraceId.ToHexString();
        await context.Response.WriteAsJsonAsync(problem, options: (JsonSerializerOptions?)null,
            contentType: "application/problem+json", cancellationToken: token);
    }
}
