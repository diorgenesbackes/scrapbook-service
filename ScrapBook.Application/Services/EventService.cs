using ScrapBook.Application.Commands;
using ScrapBook.Domain.Enums;
using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Clocks;
using ScrapBook.Domain.Policies;
namespace ScrapBook.Application.Services;

public sealed class EventService(IEventRepository repository, IUnitOfWork unitOfWork, IEventCursorService cursorCodec, BusinessClock clock) : IEventService
{
    public async Task<EventDetailsModel> CreateAsync(CurrentActorModel actor, CreateEventCommand command, Guid correlation, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var state = EventPolicy.Create(actor.AccountId, command.Name, command.StartsAt, command.WelcomeMessage,
            command.OpeningMode, command.OpeningDate, BusinessClock.ToCivil(now));
        var saved = await unitOfWork.ExecuteAsync(token => repository.CreateAsync(state, now, correlation, token), cancellationToken);
        return EventDetailsModel.Project(saved, clock);
    }

    public async Task<EventPageModel> ListAsync(CurrentActorModel actor, int limit, string? cursor, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50) throw new EventApplicationException(EventFailureKindEnum.Validation,
            new Dictionary<string, string[]> { ["limit"] = ["Use um limite de 1 a 50."] });
        var boundary = cursor is null ? null : cursorCodec.Decode(cursor, actor.AccountId);
        var records = await repository.ListAsync(actor.AccountId, limit + 1, boundary, cancellationToken);
        var hasMore = records.Count > limit;
        var items = records.Take(limit).ToArray();
        var next = hasMore ? cursorCodec.Encode(new EventCursorModel(actor.AccountId, items[^1].CreatedAt, items[^1].Id)) : null;
        return new(items, next, hasMore, clock.UtcNow);
    }

    public async Task<EventDetailsModel> GetAsync(CurrentActorModel actor, long eventId, CancellationToken cancellationToken)
    {
        var state = await repository.GetAsync(actor.AccountId, eventId, cancellationToken)
            ?? throw new EventApplicationException(EventFailureKindEnum.NotFound);
        return EventDetailsModel.Project(state, clock);
    }

    public async Task<EventDetailsModel> UpdateAsync(CurrentActorModel actor, long eventId, UpdateEventCommand command,
        Guid correlation, CancellationToken cancellationToken)
    {
        var name = EventPolicy.NormalizeName(command.Name);
        var welcome = EventPolicy.NormalizeWelcome(command.WelcomeMessage);
        EventPolicy.Window(command.StartsAt);
        var saved = await unitOfWork.ExecuteAsync(token => repository.MutateAsync(actor.AccountId, eventId, command.ExpectedVersion,
            EventMutationEnum.Structure, state => EventPolicy.UpdateStructure(state, name, command.StartsAt, welcome, clock.CivilNow),
            correlation, token), cancellationToken);
        return EventDetailsModel.Project(saved, clock);
    }

    public async Task<EventDetailsModel> UpdateAccessAsync(CurrentActorModel actor, long eventId, UpdateEventAccessCommand command,
        Guid correlation, CancellationToken cancellationToken)
    {
        var saved = await unitOfWork.ExecuteAsync(token => repository.MutateAsync(actor.AccountId, eventId, command.ExpectedVersion,
            EventMutationEnum.Access, state => EventPolicy.UpdateAccess(state, command.OpeningMode, command.OpeningDate, clock.CivilNow),
            correlation, token), cancellationToken);
        return EventDetailsModel.Project(saved, clock);
    }
}
