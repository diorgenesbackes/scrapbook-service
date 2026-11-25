namespace ScrapBook.Domain.Models;

public sealed record PhotoObjectModel(string VersionId, long Size, DateTimeOffset LastModifiedAt, string? SourceVersion,
    byte[]? Sha256);
