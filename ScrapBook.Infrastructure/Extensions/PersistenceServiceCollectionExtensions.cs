using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Services;
using ScrapBook.Infrastructure.DbContexts;
using ScrapBook.Infrastructure.Repositories;
using ScrapBook.Infrastructure.Services;
using ScrapBook.Infrastructure.UnitOfWorks;

namespace ScrapBook.Infrastructure.Extensions;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, string? connectionString)
    {
        services.TryAddSingleton(_ => new EventTelemetryService("unknown", "development"));
        services.AddDbContext<ScrapBookDbContext>(options => options
            .UseNpgsql(connectionString, postgres => postgres.SetPostgresVersion(18, 0).CommandTimeout(4))
            .EnableSensitiveDataLogging(false).EnableDetailedErrors(false));
        services.AddScoped<IUnitOfWork, ScrapBookUnitOfWork>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        return services;
    }

    public static IServiceCollection AddDevelopmentIdentity(this IServiceCollection services, bool enabled, long accountId)
    {
        services.AddScoped<ICurrentActorService>(provider => new DevelopmentCurrentActorService(enabled, accountId,
            provider.GetRequiredService<IAccountRepository>()));
        return services;
    }
}
