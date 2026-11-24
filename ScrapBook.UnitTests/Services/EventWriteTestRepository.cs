using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class EventWriteTestRepository : IEventWriteRepository
{
    public EventEntity? Item { get; set; }
    public AuditEntryEntity? Audit { get; private set; }
    public EventWriteTestTransaction Transaction { get; private set; } = new();
    public bool FailAudit { get; set; }

    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        Transaction = new EventWriteTestTransaction();
        return Task.FromResult<IEventTransaction>(Transaction);
    }

    public Task<EventEntity?> FindLinkedForUpdateAsync(long accountId, long eventId, CancellationToken cancellationToken) =>
        Task.FromResult(accountId == 9 && Item?.Id == eventId ? Item : null);

    public Task<EventEntity?> FindForUpdateAsync(long eventId, CancellationToken cancellationToken) =>
        Task.FromResult(Item?.Id == eventId ? Item : null);
    public Task SaveDetailsAsync(EventEntity item, DateTimeOffset updatedAt, CancellationToken cancellationToken) =>
        Task.CompletedTask;

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
