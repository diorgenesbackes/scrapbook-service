using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaCleanupStorageStub : IMediaCleanupStorage
{
    public string? ExpiredVersion { get; set; } = "discovered-generated-version";
    public Func<CancellationToken, Task> DeleteAsync { get; set; } = _ => Task.CompletedTask;
    public List<(string Bucket, string Key, DateTimeOffset Cutoff, CancellationToken Token)> Lookups { get; } = [];
    public List<(string Bucket, string Key, string VersionId, CancellationToken Token)> Deletions { get; } = [];

    public Task<string?> FindExpiredVersionAsync(string bucket, string key, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lookups.Add((bucket, key, cutoff, cancellationToken));
        return Task.FromResult(ExpiredVersion);
    }

    public Task DeleteVersionAsync(string bucket, string key, string versionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deletions.Add((bucket, key, versionId, cancellationToken));
        return DeleteAsync(cancellationToken);
    }
}
