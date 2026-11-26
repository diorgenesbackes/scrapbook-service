using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class PhotoSubmissionRepositoryStub(EventEntity item) : IPhotoSubmissionRepository
{
    public PhotoSubmissionEntity? Submission { get; set; }
    public CreatePhotoSubmissionCommand? Admitted { get; private set; }
    public HashSet<string> ReceivedVersions { get; } = [];
    public List<PhotoReceiptModel> Pinned { get; } = [];
    public (DateTimeOffset DueAt, string? Error)? Scheduled { get; private set; }
    public (string State, string Code)? Terminated { get; private set; }
    public bool ConfirmationAccepted { get; set; } = true;
    public List<ConfirmPhotoSubmissionCommand> Confirmations { get; } = [];
    public PhotoCleanupModel? Cleanup { get; set; }
    public (DateTimeOffset Now, DateTimeOffset TerminalBefore)? CleanupWindow { get; private set; }
    public PhotoCleanupModel? MarkedDeleted { get; private set; }

    public Task<EventEntity?> LockEventAsync(long eventId, CancellationToken cancellationToken) =>
        Task.FromResult<EventEntity?>(item.Id == eventId ? item : null);

    public Task InsertAsync(CreatePhotoSubmissionCommand command, CancellationToken cancellationToken)
    {
        Admitted = command;
        return Task.CompletedTask;
    }

    public Task<PhotoSubmissionEntity?> FindForRecordAsync(long recordId, CancellationToken cancellationToken) =>
        Task.FromResult(Submission?.RecordId == recordId ? Submission : null);

    public Task<PhotoSubmissionEntity?> FindForObjectAsync(string bucket, string key, CancellationToken cancellationToken) =>
        Task.FromResult(Submission?.Bucket == bucket && Submission.TemporaryKey == key ? Submission : null);

    public Task<PhotoSubmissionEntity?> FindWorkAsync(DateTimeOffset now, DateTime expiryBefore, CancellationToken cancellationToken) =>
        Task.FromResult(Submission);

    public Task<bool> TryLeaseAsync(long sessionId, string state, Guid lease, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken cancellationToken) => Task.FromResult(true);

    public Task ScheduleAsync(long sessionId, Guid lease, DateTimeOffset dueAt, string? error, CancellationToken cancellationToken)
    {
        Scheduled = (dueAt, error);
        return Task.CompletedTask;
    }

    public Task StoreReceiptAsync(long storageObjectId, PhotoReceiptModel receipt, DateTimeOffset cleanupAfter,
        CancellationToken cancellationToken)
    {
        ReceivedVersions.Add(receipt.VersionId);
        return Task.CompletedTask;
    }

    public Task PinAsync(PhotoSubmissionEntity submission, PhotoReceiptModel receipt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Pinned.Add(receipt);
        return Task.CompletedTask;
    }

    public Task<bool> HasReceiptAsync(long storageObjectId, string versionId, CancellationToken cancellationToken) =>
        Task.FromResult(ReceivedVersions.Contains(versionId));

    public Task<bool> ConfirmAsync(ConfirmPhotoSubmissionCommand command, CancellationToken cancellationToken)
    {
        if (ConfirmationAccepted) Confirmations.Add(command);
        return Task.FromResult(ConfirmationAccepted);
    }

    public Task TerminateAsync(PhotoSubmissionEntity submission, Guid lease, string state, string code, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Terminated = (state, code);
        return Task.CompletedTask;
    }

    public Task<PhotoCleanupModel?> FindCleanupAsync(DateTimeOffset now, DateTimeOffset terminalBefore,
        CancellationToken cancellationToken)
    {
        CleanupWindow = (now, terminalBefore);
        return Task.FromResult(Cleanup);
    }

    public Task MarkDeletedAsync(PhotoCleanupModel cleanup, DateTimeOffset now, CancellationToken cancellationToken)
    {
        MarkedDeleted = cleanup;
        return Task.CompletedTask;
    }
}
