using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScrapBook.Application.Modules.Identity;

namespace ScrapBook.Infrastructure.Modules.Identity;

internal sealed class DevelopmentAccountReader(IdentityDbContext database) : IDevelopmentAccountReader
{
    public Task<bool> IsActiveDevelopmentAccountAsync(long accountId, CancellationToken cancellationToken) =>
        database.Accounts.AsNoTracking().AnyAsync(a => a.Id == accountId && a.State == "active"
            && a.EmailVerified && a.Subject != null && a.Subject.StartsWith("development:gestao-eventos:")
            && a.Email != null && a.Email.EndsWith("@example.invalid"), cancellationToken);
}

public sealed class DevelopmentCurrentActor(bool enabled, long accountId, IDevelopmentAccountReader accounts) : ICurrentActor
{
    public async Task<CurrentActor?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!enabled || accountId <= 0) return null;
        return await accounts.IsActiveDevelopmentAccountAsync(accountId, cancellationToken)
            ? new CurrentActor(accountId) : null;
    }
}

public static class IdentityPersistenceRegistration
{
    public static IServiceCollection AddDevelopmentIdentity(this IServiceCollection services,
        string? connectionString, bool enabled, long accountId)
    {
        services.AddDbContext<IdentityDbContext>(options => options
            .UseNpgsql(connectionString, postgres => postgres.SetPostgresVersion(18, 0).CommandTimeout(4))
            .EnableSensitiveDataLogging(false).EnableDetailedErrors(false));
        services.AddScoped<IDevelopmentAccountReader, DevelopmentAccountReader>();
        services.AddScoped<ICurrentActor>(provider => new DevelopmentCurrentActor(enabled, accountId,
            provider.GetRequiredService<IDevelopmentAccountReader>()));
        return services;
    }
}
