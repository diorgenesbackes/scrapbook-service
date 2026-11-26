using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IVideoSubmissionRepository
{
    Task<long?> TryBeginMultipartAsync(long objectId, long sessionId, DateTimeOffset now, DateTimeOffset retryBefore,
        CancellationToken cancellationToken);
    Task<bool> TrySetMultipartIdAsync(long objectId, long sessionId, long claimVersion, string multipartId,
        CancellationToken cancellationToken);
    Task<PhotoSubmissionEntity?> FindMultipartCleanupAsync(DateTimeOffset terminalBefore, CancellationToken cancellationToken);
    Task MarkMultipartAbortedAsync(long objectId, DateTimeOffset eligibleAt, DateTimeOffset completedAt,
        CancellationToken cancellationToken);
    Task<DateTimeOffset?> FindReceiptAtAsync(long objectId, string versionId, string eventKind,
        CancellationToken cancellationToken);
}
