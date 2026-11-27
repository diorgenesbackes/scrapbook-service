namespace ScrapBook.Domain.Models;

public sealed record MediaFileProjectionModel(long FileId, string State, int ProfileVersion,
    IReadOnlyList<MediaVariantReferenceModel> Variants);
