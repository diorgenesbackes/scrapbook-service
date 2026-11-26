using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class VideoSubmissionRepositoryStub(PhotoSubmissionRepositoryStub photos) : IVideoSubmissionRepository
{
    public List<(long ObjectId, long SessionId)> InitializationClaims { get; } = [];
    public List<(long ObjectId, long SessionId, long ClaimVersion, string UploadId)> SavedUploads { get; } = [];

    public Task<long?> TryBeginMultipartAsync(long objectId, long sessionId, DateTimeOffset now,
        DateTimeOffset retryBefore, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializationClaims.Add((objectId, sessionId));
        return Task.FromResult<long?>(1);
    }

    public Task<bool> TrySetMultipartIdAsync(long objectId, long sessionId, long claimVersion, string multipartId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SavedUploads.Add((objectId, sessionId, claimVersion, multipartId));
        return Task.FromResult(true);
    }

    public Task<PhotoSubmissionEntity?> FindMultipartCleanupAsync(DateTimeOffset terminalBefore,
        CancellationToken cancellationToken) => Task.FromResult<PhotoSubmissionEntity?>(null);

    public Task MarkMultipartAbortedAsync(long objectId, DateTimeOffset eligibleAt, DateTimeOffset completedAt,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<DateTimeOffset?> FindReceiptAtAsync(long objectId, string versionId, string eventKind,
        CancellationToken cancellationToken) => Task.FromResult(
        photos.Received.TryGetValue((objectId, versionId), out var receipt) && receipt.EventKind == eventKind
            ? (DateTimeOffset?)receipt.ReceivedAt : null);
}
