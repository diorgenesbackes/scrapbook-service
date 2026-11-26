using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Models;

public sealed record PhotoManifestModel(Guid FileKey, string DeclaredMediaType, int SizeBytes, byte[] Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[][]? PartSha256 = null);
