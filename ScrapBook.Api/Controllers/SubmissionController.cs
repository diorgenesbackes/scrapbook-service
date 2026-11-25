using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Readers;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/public-events/{code}")]
public sealed class SubmissionController(SubmissionService submissions, TimeProvider clock) : ControllerBase
{
    private const string GetSubmission = "getSubmission";

    [HttpGet("contribution-notice")]
    public Task<SubmissionNoticeResponse> GetNoticeAsync(string code, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return submissions.GetNoticeAsync(code, cancellationToken);
    }

    [HttpPost("submission-consents")]
    [RequestSizeLimit(4096)]
    public async Task<SubmissionConsentResponse> ConfirmConsentAsync(string code, CancellationToken cancellationToken)
    {
        var ingress = clock.GetUtcNow();
        Response.Headers.CacheControl = "no-store";
        var request = await HttpJsonBodyReader.ReadAsync<ConfirmSubmissionConsentRequest>(Request, cancellationToken);
        return await submissions.ConfirmConsentAsync(code, request, ingress, cancellationToken);
    }

    [HttpPost("submissions")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<SubmissionStatusResponse>> CreateAsync(string code, CancellationToken cancellationToken)
    {
        var ingress = clock.GetUtcNow();
        Response.Headers.CacheControl = "no-store";
        var values = Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || !TryReadKey(values[0], out var attemptKey))
        {
            throw new EventRequestValidationException("idempotencyKey");
        }
        var secret = ReadSecret() ?? throw new EventRequestValidationException("authorization");
        var request = await HttpJsonBodyReader.ReadAsync<CreateSubmissionRequest>(Request, cancellationToken);
        var result = await submissions.CreateAsync(code, attemptKey, secret, request, ingress, clock.GetUtcNow(),
            CorrelationMiddleware.GetCorrelation(HttpContext), cancellationToken);
        return result.Created ? CreatedAtRoute(GetSubmission, new { code, attemptKey }, result.Status) : Ok(result.Status);
    }

    [HttpGet("submissions/{attemptKey}", Name = GetSubmission)]
    public Task<SubmissionStatusResponse> GetAsync(string code, string attemptKey, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!TryReadKey(attemptKey, out var key))
        {
            throw new SubmissionAttemptNotFoundException();
        }
        var secret = ReadSecret() ?? throw new SubmissionAttemptNotFoundException();
        return submissions.GetAsync(code, key, secret, cancellationToken);
    }

    private byte[]? ReadSecret()
    {
        var values = Request.Headers.Authorization;
        return values.Count == 1 && AuthenticationHeaderValue.TryParse(values[0], out var auth)
            && string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            && auth.Parameter is { Length: 64 } secret && secret.All(Uri.IsHexDigit)
            ? Convert.FromHexString(secret) : null;
    }

    private static bool TryReadKey(string? value, out Guid key) => Guid.TryParseExact(value, "D", out key) && key != Guid.Empty;
}
