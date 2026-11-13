using Microsoft.EntityFrameworkCore;
using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.DbContexts;

namespace ScrapBook.Infrastructure.Repositories;

internal sealed class PublicEventRepository(ScrapBookDbContext database, EventTelemetryService telemetry) : IPublicEventRepository
{
    public async Task<PublicEventModel?> GetAsync(string code, CancellationToken cancellationToken)
    {
        using var dependency = telemetry.Dependency("postgresql", "events.sql.public_get");
        var result = await database.Events.AsNoTracking().Where(row => row.Code == code)
            .Select(row => new PublicEventModel(row.Code, row.Name, row.WelcomeMessage)).SingleOrDefaultAsync(cancellationToken);
        dependency.Complete();
        return result;
    }
}
