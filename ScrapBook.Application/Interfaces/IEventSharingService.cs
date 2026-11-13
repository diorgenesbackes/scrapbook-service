using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Interfaces;

public interface IEventSharingService
{
    Task<EventSharingResponse> GetAsync(CurrentActorModel actor, long eventId, CancellationToken cancellationToken);
}
