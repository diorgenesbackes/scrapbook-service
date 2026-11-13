using ScrapBook.Application.Modules.Identity;
using ScrapBook.Domain.Common;
using ScrapBook.Domain.Modules.Events;

namespace ScrapBook.Application.Modules.Events;

public sealed class EventService(IEventStore store, IEventUnitOfWork unitOfWork, IEventCursorCodec cursorCodec, BusinessClock clock)
{
    public async Task<EventDetails> CreateAsync(CurrentActor actor, CreateEventCommand command, Guid correlation, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var state = EventRules.Create(actor.AccountId, command.Name, command.StartsAt, command.WelcomeMessage,
            command.OpeningMode, command.OpeningDate, BusinessClock.ToCivil(now));
        var saved = await unitOfWork.ExecuteAsync(token => store.CreateAsync(state, now, correlation, token), cancellationToken);
        return EventDetails.Project(saved, clock);
    }

    public async Task<EventPage> ListAsync(CurrentActor actor, int limit, string? cursor, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50) throw new EventFailure(EventFailureKind.Validation,
            new Dictionary<string, string[]> { ["limit"] = ["Use um limite de 1 a 50."] });
        var boundary = cursor is null ? null : cursorCodec.Decode(cursor, actor.AccountId);
        var records = await store.ListAsync(actor.AccountId, limit + 1, boundary, cancellationToken);
        var hasMore = records.Count > limit;
        var items = records.Take(limit).ToArray();
        var next = hasMore ? cursorCodec.Encode(new EventCursor(actor.AccountId, items[^1].CreatedAt, items[^1].Id)) : null;
        return new(items, next, hasMore, clock.UtcNow);
    }

    public async Task<EventDetails> GetAsync(CurrentActor actor, long eventId, CancellationToken cancellationToken)
    {
        var state = await store.GetAsync(actor.AccountId, eventId, cancellationToken)
            ?? throw new EventFailure(EventFailureKind.NotFound);
        return EventDetails.Project(state, clock);
    }

    public async Task<EventDetails> UpdateAsync(CurrentActor actor, long eventId, UpdateEventCommand command,
        Guid correlation, CancellationToken cancellationToken)
    {
        var name = EventRules.NormalizeName(command.Name);
        var welcome = EventRules.NormalizeWelcome(command.WelcomeMessage);
        EventRules.Window(command.StartsAt);
        var saved = await unitOfWork.ExecuteAsync(token => store.MutateAsync(actor.AccountId, eventId, command.ExpectedVersion,
            EventMutation.Structure, state => EventRules.UpdateStructure(state, name, command.StartsAt, welcome, clock.CivilNow),
            correlation, token), cancellationToken);
        return EventDetails.Project(saved, clock);
    }

    public async Task<EventDetails> UpdateAccessAsync(CurrentActor actor, long eventId, UpdateEventAccessCommand command,
        Guid correlation, CancellationToken cancellationToken)
    {
        var saved = await unitOfWork.ExecuteAsync(token => store.MutateAsync(actor.AccountId, eventId, command.ExpectedVersion,
            EventMutation.Access, state => EventRules.UpdateAccess(state, command.OpeningMode, command.OpeningDate, clock.CivilNow),
            correlation, token), cancellationToken);
        return EventDetails.Project(saved, clock);
    }
}
