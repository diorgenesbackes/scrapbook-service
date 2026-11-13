using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Interfaces;

public interface IPublicEventService
{
    Task<PublicEventPresentationModel> GetAsync(string code, CancellationToken cancellationToken);
}
