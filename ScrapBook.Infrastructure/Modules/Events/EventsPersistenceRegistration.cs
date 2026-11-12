using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScrapBook.Infrastructure.Modules.Events.Persistence;
using ScrapBook.Application.Modules.Events;

namespace ScrapBook.Infrastructure.Modules.Events;

public static class EventsPersistenceRegistration
{
    public static IServiceCollection AddEventsPersistence(this IServiceCollection services, string? connectionString)
    {
        services.AddDbContext<EventsDbContext>(options => options
            .UseNpgsql(connectionString, postgres => postgres.SetPostgresVersion(18, 0).CommandTimeout(4))
            .EnableSensitiveDataLogging(false).EnableDetailedErrors(false));
        services.AddScoped<IEventUnitOfWork, EventUnitOfWork>();
        return services;
    }
}
