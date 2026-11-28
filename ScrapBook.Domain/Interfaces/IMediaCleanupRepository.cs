using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaCleanupRepository
{
    Task<IReadOnlyList<MediaCleanupObjectModel>> FindCandidatesAsync(long eventId, long afterId, int limit,
        DateTimeOffset cutoff, CancellationToken cancellationToken);
    Task<long?> ClaimAsync(long eventId, MediaCleanupObjectModel candidate, DateTimeOffset cutoff, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken);
    Task<bool> CompleteAsync(long objectId, long version, DateTimeOffset now, CancellationToken cancellationToken);
}
