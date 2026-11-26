using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IOutboxRepository
{
    Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken);
    Task<OutboxMessageEntity?> FindAsync(Guid messageId, CancellationToken cancellationToken);
    Task<OutboxMessageEntity?> FindForUpdateAsync(Guid messageId, CancellationToken cancellationToken);
    Task InsertResultAsync(StructuralLockOutboxEntity result, CancellationToken cancellationToken);
    Task ReopenAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<OutboxBacklogModel> GetBacklogAsync(string type, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(string type, int limit, Guid leaseToken,
        DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken);
    Task<bool> MarkPublishedAsync(Guid messageId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> MarkFailedAsync(Guid messageId, Guid leaseToken, bool terminal, string errorCode, DateTimeOffset now,
        DateTimeOffset availableAt, CancellationToken cancellationToken);
}
