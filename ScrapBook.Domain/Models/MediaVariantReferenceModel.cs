namespace ScrapBook.Domain.Models;

public sealed record MediaVariantReferenceModel(string Kind, long StorageObjectId, string VersionId, string MediaType);
