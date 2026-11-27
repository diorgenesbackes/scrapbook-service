namespace ScrapBook.Domain.Entities;

public sealed record MediaFileProjectionEntity(long FileId, string State, int ProfileVersion, string Variants);
