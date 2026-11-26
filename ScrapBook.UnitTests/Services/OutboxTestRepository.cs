using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class OutboxTestRepository : IOutboxRepository
{
    public OutboxMessageEntity? Origin { get; set; }
    public Guid? CurrentLease { get; set; }
    public List<(string Type, int Limit, Guid Lease, DateTimeOffset Now, DateTimeOffset Until, CancellationToken Token)> Claims
        { get; } = [];
    public List<(Guid Id, Guid Lease, DateTimeOffset Now, CancellationToken Token)> Published { get; } = [];
    public List<(Guid Id, Guid Lease, bool Terminal, string ErrorCode, DateTimeOffset Now, DateTimeOffset AvailableAt,
        CancellationToken Token)> Failures { get; } = [];

    public Task<OutboxMessageEntity?> FindAsync(Guid messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Origin?.MessageId == messageId ? Origin : null);
    }

    public Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(string type, int limit, Guid leaseToken, DateTimeOffset now,
        DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        Claims.Add((type, limit, leaseToken, now, leaseUntil, cancellationToken));
        return Task.FromResult<IReadOnlyList<OutboxMessageEntity>>([]);
    }

    public Task<bool> MarkPublishedAsync(Guid messageId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Published.Add((messageId, leaseToken, now, cancellationToken));
        return Task.FromResult(CurrentLease is null || leaseToken == CurrentLease);
    }

    public Task<bool> MarkFailedAsync(Guid messageId, Guid leaseToken, bool terminal, string errorCode, DateTimeOffset now,
        DateTimeOffset availableAt, CancellationToken cancellationToken)
    {
        Failures.Add((messageId, leaseToken, terminal, errorCode, now, availableAt, cancellationToken));
        return Task.FromResult(CurrentLease is null || leaseToken == CurrentLease);
    }
}
