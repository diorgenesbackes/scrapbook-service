using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IPhotoSubmissionRepository
{
    Task<EventEntity?> LockEventAsync(long eventId, CancellationToken cancellationToken);
    Task InsertAsync(CreatePhotoSubmissionCommand command, CancellationToken cancellationToken);
    Task<PhotoSubmissionEntity?> FindForRecordAsync(long recordId, CancellationToken cancellationToken);
    Task<PhotoSubmissionEntity?> FindForObjectAsync(string bucket, string key, CancellationToken cancellationToken);
    Task<PhotoSubmissionEntity?> FindWorkAsync(DateTimeOffset now, DateTime expiryBefore, CancellationToken cancellationToken);
    Task<bool> TryLeaseAsync(long sessionId, string state, Guid lease, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken cancellationToken);
    Task ScheduleAsync(long sessionId, Guid lease, DateTimeOffset dueAt, string? error, CancellationToken cancellationToken);
    Task StoreReceiptAsync(long storageObjectId, PhotoReceiptModel receipt, DateTimeOffset cleanupAfter, CancellationToken cancellationToken);
    Task PinAsync(PhotoSubmissionEntity submission, PhotoReceiptModel receipt, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> HasReceiptAsync(long storageObjectId, string versionId, CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(ConfirmPhotoSubmissionCommand command, CancellationToken cancellationToken);
    Task TerminateAsync(PhotoSubmissionEntity submission, Guid lease, string state, string code, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<PhotoCleanupModel?> FindCleanupAsync(DateTimeOffset now, DateTimeOffset terminalBefore, CancellationToken cancellationToken);
    Task MarkDeletedAsync(PhotoCleanupModel cleanup, DateTimeOffset now, CancellationToken cancellationToken);
}
