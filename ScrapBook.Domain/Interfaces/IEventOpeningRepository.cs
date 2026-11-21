using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IEventOpeningRepository
{
    Task<IEventOpeningTransaction> BeginAsync(CancellationToken cancellationToken);
    Task<EventEntity?> FindLinkedForUpdateAsync(long accountId, long eventId, CancellationToken cancellationToken);
    Task SaveOpeningAsync(EventEntity item, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task InsertAuditAsync(AuditEntryEntity audit, CancellationToken cancellationToken);
}
