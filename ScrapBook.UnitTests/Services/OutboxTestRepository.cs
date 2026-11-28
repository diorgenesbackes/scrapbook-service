using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class OutboxTestRepository : IOutboxRepository
{
    public OutboxMessageEntity? Origin { get; set; }
    public DateTimeOffset? PrioritizeSince { get; private set; }
    public Guid? CurrentLease { get; set; }
    public DateTimeOffset? CurrentLeaseExpiresAt { get; set; }
    public Action<StructuralLockOutboxEntity, CancellationToken>? BeforeResult { get; set; }
    public EventWriteTestTransaction Transaction { get; private set; } = new();
    public List<(string Type, int Limit, Guid Lease, DateTimeOffset Now, DateTimeOffset Until, CancellationToken Token)> Claims
    { get; } = [];
    public List<(Guid Id, Guid Lease, DateTimeOffset Now, CancellationToken Token)> Published { get; } = [];
    public List<(Guid Id, Guid Lease, bool Terminal, string ErrorCode, DateTimeOffset Now, DateTimeOffset AvailableAt,
        CancellationToken Token)> Failures
    { get; } = [];
    public List<(StructuralLockOutboxEntity Message, CancellationToken Token)> Results { get; } = [];
    public List<(Guid Id, DateTimeOffset Now, CancellationToken Token)> Reopened { get; } = [];

    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Transaction = new EventWriteTestTransaction();
        return Task.FromResult<IEventTransaction>(Transaction);
    }

    public Task<OutboxMessageEntity?> FindAsync(Guid messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Origin?.MessageId == messageId ? Origin : null);
    }

    public Task<OutboxMessageEntity?> FindForUpdateAsync(Guid messageId, CancellationToken cancellationToken) =>
        FindAsync(messageId, cancellationToken);

    public Task ReopenAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reopened.Add((messageId, now, cancellationToken));
        if (Origin?.MessageId == messageId)
        {
            Origin = Origin with { State = "pending", AttemptCount = 0, LeaseToken = null };
        }
        return Task.CompletedTask;
    }

    public Task InsertResultAsync(StructuralLockOutboxEntity result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeResult?.Invoke(result, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Results.Add((result, cancellationToken));
        return Task.CompletedTask;
    }

    public Task<OutboxBacklogModel> GetBacklogAsync(string type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new OutboxBacklogModel(0, 0, null));
    }

    public Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(string type, int limit, Guid leaseToken, DateTimeOffset now,
        DateTimeOffset leaseUntil, DateTimeOffset? prioritizeSince, CancellationToken cancellationToken)
    {
        PrioritizeSince = prioritizeSince;
        Claims.Add((type, limit, leaseToken, now, leaseUntil, cancellationToken));
        return Task.FromResult<IReadOnlyList<OutboxMessageEntity>>([]);
    }

    public Task<bool> MarkPublishedAsync(Guid messageId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Published.Add((messageId, leaseToken, now, cancellationToken));
        return Task.FromResult(RetainsLease(leaseToken, now));
    }

    public Task<bool> MarkFailedAsync(Guid messageId, Guid leaseToken, bool terminal, string errorCode, DateTimeOffset now,
        DateTimeOffset availableAt, CancellationToken cancellationToken)
    {
        Failures.Add((messageId, leaseToken, terminal, errorCode, now, availableAt, cancellationToken));
        return Task.FromResult(RetainsLease(leaseToken, now));
    }

    private bool RetainsLease(Guid leaseToken, DateTimeOffset now) => (CurrentLease is null || leaseToken == CurrentLease)
        && (CurrentLeaseExpiresAt is null || now < CurrentLeaseExpiresAt);
}
