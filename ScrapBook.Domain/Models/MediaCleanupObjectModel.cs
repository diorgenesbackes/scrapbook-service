namespace ScrapBook.Domain.Models;

public sealed record MediaCleanupObjectModel(long Id, long FileId, long Version, string Bucket, string ObjectKey,
    string Provider, string AttemptState, Guid RequestKey, string OutputPrefix, string FileKind,
    string? VersionId = null, DateTimeOffset? LastModified = null);
