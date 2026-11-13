using ScrapBook.Application.Interfaces;
using ScrapBook.Infrastructure.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ScrapBook.Infrastructure.Repositories;

internal sealed class AccountRepository(ScrapBookDbContext database) : IAccountRepository
{
    public Task<bool> IsActiveDevelopmentAccountAsync(long accountId, CancellationToken cancellationToken) =>
        database.Accounts.AsNoTracking().AnyAsync(a => a.Id == accountId && a.State == "active"
            && a.EmailVerified && a.Subject != null && a.Subject.StartsWith("development:gestao-eventos:")
            && a.Email != null && a.Email.EndsWith("@example.invalid"), cancellationToken);
}
