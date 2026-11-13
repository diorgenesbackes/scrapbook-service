using ScrapBook.Domain.Modules.Events;

namespace ScrapBook.Application.Modules.Events;

public sealed record CreateEventCommand(string? Name, DateTime StartsAt, string? WelcomeMessage,
    string OpeningMode = "scheduled", DateOnly? OpeningDate = null);
public sealed record UpdateEventCommand(string? Name, DateTime StartsAt, string? WelcomeMessage, long ExpectedVersion);
public sealed record UpdateEventAccessCommand(string OpeningMode, DateOnly? OpeningDate, long ExpectedVersion);
public sealed record EventCursor(long AccountId, DateTimeOffset CreatedAt, long EventId);
public sealed record EventListEntry(long Id, string Name, DateTime StartsAt, string OpeningMode,
    DateTime? OpensAt, long Version, DateTimeOffset CreatedAt);
public sealed record EventPage(IReadOnlyList<EventListEntry> Items, string? NextCursor, bool HasMore, DateTimeOffset ServerTime);
public enum EventMutation { Structure, Access }

public interface IEventCursorCodec
{
    string Encode(EventCursor cursor);
    EventCursor Decode(string token, long accountId);
}

public interface IEventStore
{
    Task<EventConfiguration?> GetAsync(long accountId, long eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventListEntry>> ListAsync(long accountId, int count, EventCursor? cursor, CancellationToken cancellationToken);
    Task<EventConfiguration> CreateAsync(EventConfiguration state, DateTimeOffset joinedAt, Guid correlationId, CancellationToken cancellationToken);
    Task<EventConfiguration> MutateAsync(long accountId, long eventId, long expectedVersion, EventMutation mutation,
        Func<EventConfiguration, EventChange> apply, Guid correlationId, CancellationToken cancellationToken);
}
