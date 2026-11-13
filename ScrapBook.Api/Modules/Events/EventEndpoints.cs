using ScrapBook.Application.Modules.Events;
using ScrapBook.Application.Modules.Identity;
using ScrapBook.Domain.Modules.Events;

namespace ScrapBook.Api.Modules.Events;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/events").WithTags("Events");
        group.MapPost("", CreateAsync).WithName("CreateEvent").WithSummary("Criar evento e responsável inicial")
            .Accepts<CreateEventRequest>("application/json").Produces<EventDto>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(413).ProducesProblem(415).ProducesProblem(503);
        group.MapGet("", ListAsync).WithName("ListEvents").WithSummary("Listar eventos vinculados ao responsável")
            .Produces<EventPageDto>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(503);
        group.MapGet("/{eventId}", GetAsync).WithName("GetEvent").WithSummary("Consultar configuração do evento")
            .Produces<EventDto>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
        group.MapPut("/{eventId}", UpdateAsync).WithName("UpdateEvent").WithSummary("Salvar informações do evento")
            .Accepts<UpdateEventRequest>("application/json").Produces<EventDto>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(503);
        group.MapPut("/{eventId}/access", UpdateAccessAsync).WithName("UpdateEventAccess").WithSummary("Salvar configuração de abertura")
            .Accepts<UpdateEventAccessRequest>("application/json").Produces<EventDto>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(503);
        return endpoints;
    }

    private static CurrentActor Actor(HttpContext context) => context.Items[EventsBoundary.ActorKey] as CurrentActor
        ?? throw new EventFailure(EventFailureKind.AuthenticationRequired);
    private static Guid Correlation(HttpContext context) => (Guid)context.Items[EventsBoundary.CorrelationKey]!;

    private static void Query(HttpContext context, params string[] allowed)
    {
        if (context.Request.Query.Any(item => !allowed.Contains(item.Key, StringComparer.Ordinal) || item.Value.Count != 1))
            throw EventHttpContracts.Invalid("query", "Use somente os parâmetros documentados, uma vez cada.");
        if (context.Request.Method == "GET" && (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            throw EventHttpContracts.Invalid("body", "Consultas não recebem body.");
    }

    private static async Task<IResult> CreateAsync(HttpContext context, EventService service)
    {
        Query(context);
        var request = await EventHttpContracts.ReadAsync<CreateEventRequest>(context);
        var result = await service.CreateAsync(Actor(context), new(request.Name, EventRules.ParseStartsAt(request.StartsAt),
            request.WelcomeMessage, EventHttpContracts.Mode(request.OpeningMode), EventRules.ParseOpeningDate(request.OpeningDate)),
            Correlation(context), context.RequestAborted);
        var response = EventHttpContracts.ToDto(result);
        return Results.Created("/api/v1/events/" + response.Id, response);
    }

    private static async Task<IResult> ListAsync(HttpContext context, EventService service, int? limit, string? cursor)
    {
        Query(context, "limit", "cursor");
        var result = await service.ListAsync(Actor(context), limit ?? 20, cursor, context.RequestAborted);
        return Results.Ok(EventHttpContracts.ToDto(result));
    }

    private static async Task<IResult> GetAsync(string eventId, HttpContext context, EventService service)
    {
        Query(context);
        var result = await service.GetAsync(Actor(context), EventHttpContracts.Decimal(eventId, "eventId", true), context.RequestAborted);
        return Results.Ok(EventHttpContracts.ToDto(result));
    }

    private static async Task<IResult> UpdateAsync(string eventId, HttpContext context, EventService service)
    {
        Query(context);
        var request = await EventHttpContracts.ReadAsync<UpdateEventRequest>(context);
        var result = await service.UpdateAsync(Actor(context), EventHttpContracts.Decimal(eventId, "eventId", true),
            new(request.Name, EventRules.ParseStartsAt(request.StartsAt), request.WelcomeMessage,
                EventHttpContracts.Decimal(request.ExpectedVersion, "expectedVersion", false)), Correlation(context), context.RequestAborted);
        return Results.Ok(EventHttpContracts.ToDto(result));
    }

    private static async Task<IResult> UpdateAccessAsync(string eventId, HttpContext context, EventService service)
    {
        Query(context);
        var request = await EventHttpContracts.ReadAsync<UpdateEventAccessRequest>(context);
        var result = await service.UpdateAccessAsync(Actor(context), EventHttpContracts.Decimal(eventId, "eventId", true),
            new(EventHttpContracts.Mode(request.OpeningMode), EventRules.ParseOpeningDate(request.OpeningDate),
                EventHttpContracts.Decimal(request.ExpectedVersion, "expectedVersion", false)), Correlation(context), context.RequestAborted);
        return Results.Ok(EventHttpContracts.ToDto(result));
    }
}
