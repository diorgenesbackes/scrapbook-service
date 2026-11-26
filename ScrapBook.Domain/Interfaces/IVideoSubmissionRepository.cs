using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IVideoSubmissionRepository
{
    Task<long?> TryBeginMultipartAsync(long objectId, long sessionId, DateTimeOffset now, DateTimeOffset retryBefore,
        CancellationToken cancellationToken);
    Task<bool> TrySetMultipartIdAsync(long objectId, long sessionId, long claimVersion, string multipartId,
        CancellationToken cancellationToken);
    Task<PhotoSubmissionEntity?> FindMultipartCleanupAsync(DateTimeOffset now, DateTimeOffset terminalBefore,
        string[] terminalStates, string deadlineOnlyState, CancellationToken cancellationToken);
    Task<bool> TryClaimMultipartCleanupAsync(long objectId, DateTimeOffset eligibleAt, DateTimeOffset now, Guid lease,
        DateTimeOffset leaseUntil, CancellationToken cancellationToken);
    Task MarkMultipartAbortedAsync(long objectId, Guid lease, DateTimeOffset completedAt, CancellationToken cancellationToken);
    Task ScheduleMultipartCleanupAsync(long objectId, Guid lease, DateTimeOffset now, DateTimeOffset nextAttemptAt,
        string errorCode, CancellationToken cancellationToken);
    Task<DateTimeOffset?> FindReceiptAtAsync(long objectId, string versionId, string eventKind,
        CancellationToken cancellationToken);
}
