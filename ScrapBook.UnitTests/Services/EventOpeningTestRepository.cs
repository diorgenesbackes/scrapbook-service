using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class EventOpeningTestRepository : IEventOpeningRepository
{
    public EventEntity? Item { get; set; }
    public AuditEntryEntity? Audit { get; private set; }
    public EventOpeningTestTransaction Transaction { get; private set; } = new();
    public bool FailAudit { get; set; }

    public Task<IEventOpeningTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        Transaction = new EventOpeningTestTransaction();
        return Task.FromResult<IEventOpeningTransaction>(Transaction);
    }

    public Task<EventEntity?> FindLinkedForUpdateAsync(long accountId, long eventId, CancellationToken cancellationToken) =>
        Task.FromResult(accountId == 9 && Item?.Id == eventId ? Item : null);

    public Task SaveOpeningAsync(EventEntity item, DateTimeOffset updatedAt, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task InsertAuditAsync(AuditEntryEntity audit, CancellationToken cancellationToken)
    {
        if (FailAudit)
        {
            throw new InvalidOperationException();
        }

        Audit = audit;
        return Task.CompletedTask;
    }
}
