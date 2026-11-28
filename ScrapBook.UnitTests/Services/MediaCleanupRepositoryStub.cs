using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaCleanupRepositoryStub : IMediaCleanupRepository
{
    public IReadOnlyList<MediaCleanupObjectModel> Candidates { get; set; } = [];
    public long? ClaimVersion { get; set; } = 4;
    public bool Completes { get; set; } = true;
    public List<EventWriteTestTransaction> Transactions { get; } = [];
    public List<(long EventId, long AfterId, int Limit, DateTimeOffset Cutoff, CancellationToken Token)> Pages { get; } = [];
    public List<(long EventId, MediaCleanupObjectModel Candidate, string VersionId, Guid Lease, DateTimeOffset Cutoff, DateTimeOffset Now,
        CancellationToken Token)> Claims { get; } = [];
    public List<(long ObjectId, long Version, Guid Lease, DateTimeOffset Now, CancellationToken Token)> Completions { get; } = [];

    public Task<IReadOnlyList<MediaCleanupObjectModel>> FindCandidatesAsync(long eventId, long afterId, int limit,
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pages.Add((eventId, afterId, limit, cutoff, cancellationToken));
        return Task.FromResult(Candidates);
    }

    public Task<long?> ClaimAsync(long eventId, MediaCleanupObjectModel candidate, string versionId, Guid lease,
        DateTimeOffset cutoff, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Claims.Add((eventId, candidate, versionId, lease, cutoff, now, cancellationToken));
        return Task.FromResult(ClaimVersion);
    }

    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = new EventWriteTestTransaction();
        Transactions.Add(transaction);
        return Task.FromResult<IEventTransaction>(transaction);
    }

    public Task<bool> CompleteAsync(long objectId, long version, Guid lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Completions.Add((objectId, version, lease, now, cancellationToken));
        return Task.FromResult(Completes);
    }
}
