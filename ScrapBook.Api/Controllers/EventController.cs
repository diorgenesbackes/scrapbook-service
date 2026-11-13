using Microsoft.AspNetCore.Mvc;
using ScrapBook.Api.Mappers;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Requests;
using ScrapBook.Api.Responses;
using ScrapBook.Api.Validators;
using ScrapBook.Application.Enums;
using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Models;
using ScrapBook.Domain.Policies;

namespace ScrapBook.Api.Controllers;

[ApiController]
[Route("/api/v1/events")]
[Tags("Events")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(401, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(413, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(415, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(500, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class EventController(IEventService service) : ControllerBase
{
    private CurrentActorModel Actor => HttpContext.Items[EventRequestMiddleware.ActorKey] as CurrentActorModel
        ?? throw new EventApplicationException(EventFailureKindEnum.AuthenticationRequired);
    private Guid Correlation => (Guid)HttpContext.Items[EventRequestMiddleware.CorrelationKey]!;

    [HttpPost(Name = "CreateEvent")]
    [Consumes("application/json")]
    [ProducesResponseType<EventResponse>(201)]
    public async Task<ActionResult<EventResponse>> CreateAsync([FromBody] CreateEventRequest request)
    {
        EventRequestValidator.ValidateQuery(HttpContext);
        var result = await service.CreateAsync(Actor, new(request.Name, EventPolicy.ParseStartsAt(request.StartsAt),
            request.WelcomeMessage, EventRequestValidator.Mode(request.OpeningMode), EventPolicy.ParseOpeningDate(request.OpeningDate)),
            Correlation, HttpContext.RequestAborted);
        var response = EventResponseMapper.ToResponse(result);
        return Created("/api/v1/events/" + response.Id, response);
    }

    [HttpGet(Name = "ListEvents")]
    [ProducesResponseType<EventPageResponse>(200)]
    public async Task<ActionResult<EventPageResponse>> ListAsync([FromQuery] int? limit, [FromQuery] string? cursor)
    {
        EventRequestValidator.ValidateQuery(HttpContext, "limit", "cursor");
        var result = await service.ListAsync(Actor, limit ?? 20, cursor, HttpContext.RequestAborted);
        return Ok(EventResponseMapper.ToResponse(result));
    }

    [HttpGet("{eventId}", Name = "GetEvent")]
    [ProducesResponseType<EventResponse>(200)]
    public async Task<ActionResult<EventResponse>> GetAsync([FromRoute] string eventId)
    {
        EventRequestValidator.ValidateQuery(HttpContext);
        var result = await service.GetAsync(Actor, EventRequestValidator.Decimal(eventId, "eventId", true), HttpContext.RequestAborted);
        return Ok(EventResponseMapper.ToResponse(result));
    }

    [HttpPut("{eventId}", Name = "UpdateEvent")]
    [Consumes("application/json")]
    [ProducesResponseType<EventResponse>(200)]
    public async Task<ActionResult<EventResponse>> UpdateAsync([FromRoute] string eventId, [FromBody] UpdateEventRequest request)
    {
        EventRequestValidator.ValidateQuery(HttpContext);
        var result = await service.UpdateAsync(Actor, EventRequestValidator.Decimal(eventId, "eventId", true),
            new(request.Name, EventPolicy.ParseStartsAt(request.StartsAt), request.WelcomeMessage,
                EventRequestValidator.Decimal(request.ExpectedVersion, "expectedVersion", false)), Correlation, HttpContext.RequestAborted);
        return Ok(EventResponseMapper.ToResponse(result));
    }

    [HttpPut("{eventId}/access", Name = "UpdateEventAccess")]
    [Consumes("application/json")]
    [ProducesResponseType<EventResponse>(200)]
    public async Task<ActionResult<EventResponse>> UpdateAccessAsync([FromRoute] string eventId, [FromBody] UpdateEventAccessRequest request)
    {
        EventRequestValidator.ValidateQuery(HttpContext);
        var result = await service.UpdateAccessAsync(Actor, EventRequestValidator.Decimal(eventId, "eventId", true),
            new(EventRequestValidator.Mode(request.OpeningMode), EventPolicy.ParseOpeningDate(request.OpeningDate),
                EventRequestValidator.Decimal(request.ExpectedVersion, "expectedVersion", false)), Correlation, HttpContext.RequestAborted);
        return Ok(EventResponseMapper.ToResponse(result));
    }
}
