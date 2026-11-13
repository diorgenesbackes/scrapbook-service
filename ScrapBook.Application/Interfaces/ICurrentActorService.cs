using ScrapBook.Application.Models;
namespace ScrapBook.Application.Interfaces;

public interface ICurrentActorService
{
    Task<CurrentActorModel?> ResolveAsync(CancellationToken cancellationToken);
}
