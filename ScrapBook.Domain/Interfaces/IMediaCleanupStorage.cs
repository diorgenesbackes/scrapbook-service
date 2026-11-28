namespace ScrapBook.Domain.Interfaces;

public interface IMediaCleanupStorage
{
    Task<bool> DeleteExpiredVersionsAsync(string bucket, string key, DateTimeOffset cutoff, CancellationToken cancellationToken);
}
