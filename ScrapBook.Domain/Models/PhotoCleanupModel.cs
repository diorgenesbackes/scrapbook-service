namespace ScrapBook.Domain.Models;

public sealed record PhotoCleanupModel(long StorageObjectId, string Bucket, string Key, string VersionId);
