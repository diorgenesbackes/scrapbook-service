using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Interfaces;

public interface IPublicEventService
{
    Task<PublicEventModel> GetAsync(string code, CancellationToken cancellationToken);
}
