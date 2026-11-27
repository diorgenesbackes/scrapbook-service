namespace ScrapBook.Domain.Models;

public sealed record MediaStoredVariantModel(string Bucket, string Key, string VersionId, long Size, byte[] Sha256,
    DateTimeOffset LastModifiedAt);
