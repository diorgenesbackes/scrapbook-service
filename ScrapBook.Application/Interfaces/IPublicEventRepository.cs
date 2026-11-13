using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Interfaces;

public interface IPublicEventRepository
{
    Task<PublicEventModel?> GetAsync(string code, CancellationToken cancellationToken);
}
