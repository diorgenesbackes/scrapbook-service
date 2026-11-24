using Microsoft.AspNetCore.Mvc;
using ScrapBook.Api.Guards;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Options;
using ScrapBook.Api.Readers;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Api.Controllers;

[ApiController]
[Route("api/v1/invitations")]
[RequestSizeLimit(16 * 1024)]
public sealed class InvitationController(
    InvitationAcceptanceService invitations, PublicUiOriginOptions ui, IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost("preview", Name = "previewEventInvitation")]
    public async Task<InvitationPreviewResponse> PreviewAsync(CancellationToken cancellationToken)
    {
        var account = EventRequestGuard.RequireAccount(HttpContext);
        var token = await ReadTokenAsync(cancellationToken);
        return await invitations.PreviewAsync(account, token, cancellationToken);
    }

    [HttpPost("accept", Name = "acceptEventInvitation")]
    public async Task<InvitationAcceptanceResponse> AcceptAsync(CancellationToken cancellationToken)
    {
        var account = EventRequestGuard.RequireAccount(HttpContext);
        var token = await ReadTokenAsync(cancellationToken);
        return await invitations.AcceptAsync(account, token, CorrelationMiddleware.GetCorrelation(HttpContext), cancellationToken);
    }

    private async Task<string> ReadTokenAsync(CancellationToken cancellationToken)
    {
        EventRequestGuard.VerifyOrigin(Request, ui, environment);
        var request = await HttpJsonBodyReader.ReadAsync<InvitationTokenRequest>(Request, cancellationToken);
        return request.Token ?? throw new EventRequestValidationException("token");
    }
}
