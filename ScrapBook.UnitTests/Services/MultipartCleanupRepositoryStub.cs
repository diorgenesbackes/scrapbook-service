using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class MultipartCleanupRepositoryStub : IVideoSubmissionRepository
{
    public Queue<PhotoSubmissionEntity> Candidates { get; } = new();
    public bool ClaimAccepted { get; set; } = true;
    public List<(long ObjectId, DateTimeOffset EligibleAt, DateTimeOffset Now, Guid Lease, DateTimeOffset LeaseUntil)> Claims { get; } = [];
    public List<(long ObjectId, Guid Lease, DateTimeOffset CompletedAt)> Completed { get; } = [];
    public List<(long ObjectId, Guid Lease, DateTimeOffset Now, DateTimeOffset NextAttemptAt, string Error)> Scheduled { get; } = [];

    public Task<PhotoSubmissionEntity?> FindMultipartCleanupAsync(DateTimeOffset now, DateTimeOffset terminalBefore,
        string[] terminalStates, string deadlineOnlyState, CancellationToken cancellationToken) =>
        Task.FromResult(Candidates.TryDequeue(out var file) ? file : null);

    public Task<bool> TryClaimMultipartCleanupAsync(long objectId, DateTimeOffset eligibleAt, DateTimeOffset now,
        Guid lease, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        Claims.Add((objectId, eligibleAt, now, lease, leaseUntil));
        return Task.FromResult(ClaimAccepted);
    }

    public Task MarkMultipartAbortedAsync(long objectId, Guid lease, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        Completed.Add((objectId, lease, completedAt));
        return Task.CompletedTask;
    }

    public Task ScheduleMultipartCleanupAsync(long objectId, Guid lease, DateTimeOffset now, DateTimeOffset nextAttemptAt,
        string errorCode, CancellationToken cancellationToken)
    {
        Scheduled.Add((objectId, lease, now, nextAttemptAt, errorCode));
        return Task.CompletedTask;
    }

    public Task<long?> TryBeginMultipartAsync(long objectId, long sessionId, DateTimeOffset now, DateTimeOffset retryBefore,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> TrySetMultipartIdAsync(long objectId, long sessionId, long claimVersion, string multipartId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DateTimeOffset?> FindReceiptAtAsync(long objectId, string versionId, string eventKind,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
