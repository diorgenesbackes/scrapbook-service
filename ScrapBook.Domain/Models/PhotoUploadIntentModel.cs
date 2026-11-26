namespace ScrapBook.Domain.Models;

public sealed record PhotoUploadIntentModel(PhotoManifestModel Manifest, int Position, string TemporaryKey, string OriginalKey);
