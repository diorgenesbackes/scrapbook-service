using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Options;
using ScrapBook.Api.Readers;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Api.Controllers;

[ApiController]
[Route("api/v1/events")]
public sealed class EventController(
    EventService events, EventOpeningService opening, PublicUiOriginOptions ui, IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet(Name = "listEvents")]
    public async Task<EventListResponse> ListAsync(CancellationToken cancellationToken)
    {
        var account = RequireAccount();
        return await events.ListAsync(account, ReadQuery("cursor"), ReadQuery("pageSize"), cancellationToken);
    }

    [HttpGet("{eventId}", Name = "getEvent")]
    public async Task<EventResponse> GetAsync(string eventId, CancellationToken cancellationToken)
    {
        var account = RequireAccount();
        return await events.GetAsync(account, eventId, cancellationToken);
    }

    [HttpPost(Name = "createEvent")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> CreateAsync(CancellationToken cancellationToken)
    {
        var account = RequireAccount();
        VerifyOrigin();
        var keys = Request.Headers["Idempotency-Key"];
        var request = await HttpJsonBodyReader.ReadAsync<CreateEventRequest>(Request, cancellationToken);
        var result = await events.CreateAsync(account, keys.Count == 1 ? keys[0] : null, request, cancellationToken);
        return result.IsReplay ? Ok(result.Event) : CreatedAtRoute("getEvent", new { eventId = result.Event.Id }, result.Event);
    }

    [HttpPut("{eventId}/opening-policy", Name = "replaceEventOpeningPolicy")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<EventResponse> ReplaceOpeningAsync(string eventId, CancellationToken cancellationToken)
    {
        var account = RequireAccount();
        VerifyOrigin();
        var request = await HttpJsonBodyReader.ReadAsync<ReplaceEventOpeningRequest>(Request, cancellationToken);
        return await opening.ReplaceAsync(account, eventId, request,
            CorrelationMiddleware.GetCorrelation(HttpContext), cancellationToken);
    }

    private long RequireAccount()
    {
        Response.Headers.CacheControl = "no-store";
        if (User.Identity?.IsAuthenticated != true
            || !long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None,
                CultureInfo.InvariantCulture, out var accountId) || accountId < 1)
        {
            throw new EventIdentityRequiredException();
        }

        return accountId;
    }

    private string? ReadQuery(string name)
    {
        var values = Request.Query[name];
        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => throw new EventRequestValidationException(name)
        };
    }

    private void VerifyOrigin()
    {
        var expected = ui.Origin;
        if (environment.IsDevelopment() && Request.Host.Host is "127.0.0.1" or "localhost" or "[::1]")
        {
            expected ??= $"{Request.Scheme}://{Request.Host}";
        }

        var origins = Request.Headers.Origin;
        if (expected is null || origins.Count != 1 || !string.Equals(origins[0], expected, StringComparison.Ordinal))
        {
            throw new EventOriginRejectedException();
        }
    }
}
