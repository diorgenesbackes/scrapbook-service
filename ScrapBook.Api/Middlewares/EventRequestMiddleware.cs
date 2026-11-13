using ScrapBook.Api.Options;
using ScrapBook.Api.Validators;
using ScrapBook.Application.Enums;
using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Mappers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ScrapBook.Api.Middlewares;

public sealed class EventRequestMiddleware(RequestDelegate next, ILogger<EventRequestMiddleware> logger)
{
    public const string ActorKey = "ScrapBook.Events.Actor";
    public const string CorrelationKey = "ScrapBook.Events.Correlation";
    public async Task InvokeAsync(HttpContext context, ICurrentActorService currentActor, DevelopmentIdentityOptions identity, EventTelemetryService telemetry)
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
            "CreateEvent" => "events.create",
            "ListEvents" => "events.list",
            "GetEvent" => "events.get",
            "UpdateEvent" => "events.update_structure",
            "UpdateEventAccess" => "events.update_access",
            _ => "events.unknown"
        };
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "/api/v1/events/*";
        route = "/" + route.TrimStart('/');
        var start = Stopwatch.GetTimestamp();
        EventFailureKindEnum? failureKind = null;
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
            DevelopmentOriginValidator.Validate(context, identity);
            using (var identityDependency = telemetry.Dependency("identity", "identity.resolve"))
            {
                context.Items[ActorKey] = await currentActor.ResolveAsync(budget.Token)
                    ?? throw new EventApplicationException(EventFailureKindEnum.AuthenticationRequired);
                identityDependency.Complete();
            }
            if (context.Request.Method is "POST" or "PUT") EventRequestValidator.ValidateBodyHeaders(context);
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
                || failure.Kind == EventFailureKindEnum.DependencyUnavailable;
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
            var eventId = failureKind == EventFailureKindEnum.Unexpected ? new EventId(4003, "EventsUnexpectedFailure")
                : status >= 400 ? new EventId(4002, "EventsRequestRejected") : new EventId(4000, "EventsRequestCompleted");
            logger.Log(status >= 500 ? LogLevel.Error : status >= 400 ? LogLevel.Warning : LogLevel.Information, eventId,
                "{EventName} {ServiceName} {Component} {Module} {Operation} {HttpMethod} {RouteTemplate} {Status} {Outcome} {DurationMs} {CorrelationId} {TraceId} {SpanId} {RequestId} {ServiceVersion} {Environment} {InstanceId} {ErrorCode} {ExceptionType}",
                eventId.Name, "scrapbook-backend", "api", "events", operation, context.Request.Method, route, status, outcome, durationMs, correlation,
                Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.TraceId.ToHexString() : null,
                Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.SpanId.ToHexString() : null,
                context.TraceIdentifier, telemetry.Version, telemetry.EnvironmentName, telemetry.InstanceId, failureKind?.ToString(), exceptionType);
        }
    }
    public static EventApplicationException Sanitize(Exception error) => error switch
    {
        EventApplicationException known => known,
        EventRuleException rule => new(rule.Failure switch
        {
            EventRuleFailureEnum.StructureLocked => EventFailureKindEnum.StructureLocked,
            EventRuleFailureEnum.OpeningLocked => EventFailureKindEnum.OpeningLocked,
            _ => EventFailureKindEnum.Validation
        }, rule.Field is null ? null : new Dictionary<string, string[]> { [rule.Field] = [rule.Message] }),
        BadHttpRequestException { StatusCode: 413 } => new(EventFailureKindEnum.TooLarge),
        BadHttpRequestException or JsonException => new(EventFailureKindEnum.Validation),
        DbUpdateException or NpgsqlException or TimeoutException or OperationCanceledException => PersistenceExceptionMapper.Translate(error),
        InvalidOperationException { InnerException: NpgsqlException } => PersistenceExceptionMapper.Translate(error),
        _ => new(EventFailureKindEnum.Unexpected)
    };
    private static async Task WriteProblemAsync(HttpContext context, EventApplicationException failure, Guid correlation, CancellationToken token)
    {
        var (status, title, code, detail) = failure.Kind switch
        {
            EventFailureKindEnum.Validation => (400, "Bad Request", "REQUEST_VALIDATION_FAILED", "Revise os dados informados."),
            EventFailureKindEnum.AuthenticationRequired => (401, "Unauthorized", "IDENTITY_AUTHENTICATION_REQUIRED", "A identidade local de desenvolvimento não está configurada ou não é válida."),
            EventFailureKindEnum.OriginNotAllowed => (403, "Forbidden", "REQUEST_ORIGIN_NOT_ALLOWED", "Origem da requisição não permitida."),
            EventFailureKindEnum.NotFound => (404, "Not Found", "RESOURCE_NOT_FOUND", "Evento não encontrado."),
            EventFailureKindEnum.StructureLocked => (409, "Conflict", "EVENT_LOCKED", "As informações deste evento não estão disponíveis para edição."),
            EventFailureKindEnum.OpeningLocked => (409, "Conflict", "EVENT_OPENING_LOCKED", "A configuração de abertura não está disponível para alteração."),
            EventFailureKindEnum.VersionConflict => (409, "Conflict", "EVENT_VERSION_CONFLICT", "O evento foi alterado. Recarregue os dados antes de salvar novamente."),
            EventFailureKindEnum.TooLarge => (413, "Content Too Large", "REQUEST_TOO_LARGE", "A requisição excede o limite de 32 KiB."),
            EventFailureKindEnum.UnsupportedMediaType => (415, "Unsupported Media Type", "UNSUPPORTED_MEDIA_TYPE", "Envie JSON UTF-8 com Content-Type application/json."),
            EventFailureKindEnum.DependencyUnavailable => (503, "Service Unavailable", "DEPENDENCY_UNAVAILABLE", "Não foi possível confirmar a operação. Consulte o estado antes de repetir uma gravação."),
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
