using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScrapBook.Api.Validators;
using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Api.Controllers;

[ApiController]
[Route("/api/v1/public/events")]
[Tags("Public events")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(500, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class PublicEventController(IPublicEventService service) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("{code}", Name = "GetPublicEvent")]
    [ProducesResponseType<PublicEventResponse>(200)]
    public async Task<ActionResult<PublicEventResponse>> GetAsync([FromRoute] string code)
    {
        EventRequestValidator.ValidateQuery(HttpContext);
        var result = await service.GetAsync(code, HttpContext.RequestAborted);
        return Ok(new PublicEventResponse(result.Code));
    }
}
