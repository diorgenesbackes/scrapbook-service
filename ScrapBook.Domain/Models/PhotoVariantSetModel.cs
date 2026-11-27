namespace ScrapBook.Domain.Models;

public sealed record PhotoVariantSetModel(int SourceWidth, int SourceHeight, PhotoVariantModel Image, PhotoVariantModel Thumbnail);
