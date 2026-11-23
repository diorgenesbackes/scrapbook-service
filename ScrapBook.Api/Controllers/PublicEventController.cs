using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/public-events")]
public sealed class PublicEventController(PublicEventService events) : ControllerBase
{
    [HttpGet("{code?}", Name = "getPublicEvent")]
    public async Task<PublicEventResponse> GetAsync(string? code, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            return await events.GetAsync(code, budget.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new PublicEventTimeoutException(error);
        }
    }
}
