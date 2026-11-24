using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IEventWriteRepository
{
    Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken);
    Task<EventEntity?> FindLinkedForUpdateAsync(long accountId, long eventId, CancellationToken cancellationToken);
    Task<EventEntity?> FindForUpdateAsync(long eventId, CancellationToken cancellationToken);
    Task SaveDetailsAsync(EventEntity item, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task SaveOpeningAsync(EventEntity item, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task InsertAuditAsync(AuditEntryEntity audit, CancellationToken cancellationToken);
}
