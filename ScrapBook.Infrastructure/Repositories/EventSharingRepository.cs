using Microsoft.EntityFrameworkCore;
using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.DbContexts;

namespace ScrapBook.Infrastructure.Repositories;

internal sealed class EventSharingRepository(ScrapBookDbContext database, EventTelemetryService telemetry) : IEventSharingRepository
{
    public async Task<EventSharingModel?> GetAsync(long accountId, long eventId, CancellationToken cancellationToken)
    {
        using var dependency = telemetry.Dependency("postgresql", "events.sql.share_get");
        var result = await database.Events.AsNoTracking().Where(row => row.Id == eventId
            && database.Members.Any(member => member.EventId == row.Id && member.AccountId == accountId))
            .Select(row => new EventSharingModel(row.Code, row.Name)).SingleOrDefaultAsync(cancellationToken);
        dependency.Complete();
        return result;
    }
}
