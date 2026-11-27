namespace ScrapBook.Domain.Models;

public sealed record PreparedPhotoVariantModel(string Kind, int Width, int Height, MediaStoredVariantModel Storage);
