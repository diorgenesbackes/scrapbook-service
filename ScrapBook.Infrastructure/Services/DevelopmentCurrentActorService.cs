using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ScrapBook.Infrastructure.Services;

public sealed class DevelopmentCurrentActorService(bool enabled, long accountId, IAccountRepository accounts) : ICurrentActorService
{
    public async Task<CurrentActorModel?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!enabled || accountId <= 0) return null;
        return await accounts.IsActiveDevelopmentAccountAsync(accountId, cancellationToken)
            ? new CurrentActorModel(accountId) : null;
    }
}
