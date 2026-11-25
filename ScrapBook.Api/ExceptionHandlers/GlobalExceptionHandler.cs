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

        var (status, code, title) = Describe(exception);
        var correlation = CorrelationMiddleware.GetCorrelation(httpContext);
        if (status >= 500)
        {
            logger.LogError("Falha HTTP {ErrorCode} com correlação {CorrelationId}", code, correlation);
        }

        var details = new ProblemDetails
        {
            Type = "about:blank",
            Title = title,
            Status = status,
            Detail = status >= 500 && exception is not PhotoSubmissionDisabledException
                ? "Ocorreu uma falha temporária." : title
        };
        if (exception is EventRequestValidationException validation)
        {
            details.Extensions["errors"] = new Dictionary<string, string[]> { [validation.Field] = ["Valor inválido."] };
        }
        if (exception is SubmissionWindowException window)
        {
            details.Extensions["windowState"] = window.WindowState;
        }
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

    private static (int Status, string Code, string Title) Describe(Exception exception) => exception switch
    {
        EventRequestValidationException => (400, "REQUEST_VALIDATION_FAILED", "Confira os campos destacados antes de salvar."),
        EventIdentityRequiredException => (401, "IDENTITY_AUTHENTICATION_REQUIRED", "Sua sessão terminou. Entre novamente."),
        EventOriginRejectedException => (403, "REQUEST_ORIGIN_REJECTED", "Origem da solicitação recusada."),
        InvitationAccountUnconfirmedException => (403, "INVITATION_ACCOUNT_UNCONFIRMED", "Confirme seu e-mail para aceitar o convite."),
        EventNotFoundException => (404, "RESOURCE_NOT_FOUND", "Evento indisponível para esta conta."),
        PublicEventNotFoundException => (404, "RESOURCE_NOT_FOUND", "Evento indisponível."),
        InvitationNotFoundException => (404, "RESOURCE_NOT_FOUND", "Este convite não está mais disponível. Solicite um novo."),
        EventStructuralLockedException => (409, "EVENT_LOCKED", "As informações deste evento estão travadas."),
        EventOpeningLockedException => (409, "EVENT_OPENING_LOCKED", "A data de abertura deste evento está travada."),
        EventVersionConflictException => (409, "EVENT_VERSION_CONFLICT", "O evento foi alterado em outra sessão."),
        EventCreationKeyReusedException or InvitationKeyReusedException or SubmissionKeyReusedException =>
            (409, "IDEMPOTENCY_KEY_REUSED", "A chave já foi usada em outra tentativa."),
        SubmissionAttemptNotFoundException => (404, "RESOURCE_NOT_FOUND", "Envio indisponível."),
        SubmissionWindowException window => (409, "UPLOAD_WINDOW_CLOSED", window.WindowState == "before"
            ? "Novos envios ainda não estão disponíveis." : "O prazo para novos envios terminou."),
        PhotoSubmissionDisabledException => (503, "PHOTO_SUBMISSION_DISABLED", "O envio de fotos está indisponível."),
        PhotoUploadUnavailableException => (409, "UPLOAD_NOT_AVAILABLE", "O envio da foto está indisponível nesta tentativa."),
        SubmissionConsentRequiredException => (409, "SUBMISSION_CONSENT_REQUIRED", "Confirme o aviso para continuar."),
        SubmissionDeadlineExceededException => (409, "UPLOAD_COMPLETION_DEADLINE_EXCEEDED", "O prazo para concluir este envio terminou."),
        InvitationUnavailableException => (409, "INVITATION_UNAVAILABLE", "Este convite não está mais disponível. Solicite um novo."),
        InvitationEventLockedException => (409, "INVITATION_EVENT_LOCKED", "Os convites deste evento estão travados."),
        InvitationEventFullException => (409, "INVITATION_EVENT_FULL", "Este evento já tem dois responsáveis."),
        InvitationAlreadyUsedException => (409, "INVITATION_ALREADY_USED", "Este convite já foi usado."),
        InvitationAccountAlreadyLinkedException => (409, "INVITATION_ACCOUNT_ALREADY_LINKED", "Você já é responsável por este evento."),
        BadHttpRequestException { StatusCode: 413 } => (413, "REQUEST_TOO_LARGE", "Tamanho da solicitação excedido."),
        EventJsonMediaTypeException => (415, "UNSUPPORTED_MEDIA_TYPE", "Use application/json."),
        PostgresUnavailableException or PublicEventTimeoutException or InvitationProtectionUnavailableException or SubmissionUnavailableException or PhotoStorageUnavailableException =>
            (503, "DEPENDENCY_UNAVAILABLE", "Dependência indisponível"),
        _ => (500, "UNEXPECTED_ERROR", "Falha interna")
    };
}
