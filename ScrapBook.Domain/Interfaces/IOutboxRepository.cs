using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IOutboxRepository
{
    Task<OutboxMessageEntity?> FindAsync(Guid messageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(string type, int limit, Guid leaseToken,
        DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken);
    Task<bool> MarkPublishedAsync(Guid messageId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> MarkFailedAsync(Guid messageId, Guid leaseToken, bool terminal, string errorCode, DateTimeOffset now,
        DateTimeOffset availableAt, CancellationToken cancellationToken);
}
