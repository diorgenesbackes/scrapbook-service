namespace ScrapBook.Domain.Interfaces;

public interface IMediaCleanupStorage
{
    Task<string?> FindExpiredVersionAsync(string bucket, string key, DateTimeOffset cutoff, CancellationToken cancellationToken);
    Task DeleteVersionAsync(string bucket, string key, string versionId, CancellationToken cancellationToken);
}
