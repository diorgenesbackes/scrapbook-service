using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

public sealed class DevelopmentCurrentActorService(bool enabled, long accountId, IAccountRepository accounts) : ICurrentActorService
{
    public async Task<CurrentActorModel?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!enabled || accountId <= 0) return null;
        return await accounts.IsActiveDevelopmentAccountAsync(accountId, cancellationToken)
            ? new CurrentActorModel(accountId) : null;
    }
}
