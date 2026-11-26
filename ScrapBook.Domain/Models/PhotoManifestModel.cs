namespace ScrapBook.Domain.Models;

public sealed record PhotoManifestModel(Guid FileKey, string DeclaredMediaType, int SizeBytes, byte[] Sha256);
