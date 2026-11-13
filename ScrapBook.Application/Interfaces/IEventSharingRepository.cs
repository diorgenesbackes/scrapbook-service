using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Interfaces;

public interface IEventSharingRepository
{
    Task<EventSharingModel?> GetAsync(long accountId, long eventId, CancellationToken cancellationToken);
}
