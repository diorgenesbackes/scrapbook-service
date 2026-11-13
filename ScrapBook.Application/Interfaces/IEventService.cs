using ScrapBook.Application.Commands;
using ScrapBook.Application.Models;

namespace ScrapBook.Application.Interfaces;

public interface IEventService
{
    Task<EventDetailsModel> CreateAsync(CurrentActorModel actor, CreateEventCommand command, Guid correlation, CancellationToken cancellationToken);
    Task<EventPageModel> ListAsync(CurrentActorModel actor, int limit, string? cursor, CancellationToken cancellationToken);
    Task<EventDetailsModel> GetAsync(CurrentActorModel actor, long eventId, CancellationToken cancellationToken);
    Task<EventDetailsModel> UpdateAsync(CurrentActorModel actor, long eventId, UpdateEventCommand command, Guid correlation, CancellationToken cancellationToken);
    Task<EventDetailsModel> UpdateAccessAsync(CurrentActorModel actor, long eventId, UpdateEventAccessCommand command, Guid correlation, CancellationToken cancellationToken);
}
