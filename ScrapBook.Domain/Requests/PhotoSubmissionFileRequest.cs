using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Requests;

public sealed record PhotoSubmissionFileRequest(Guid FileKey, int Position, string? DeclaredMediaType, int SizeBytes,
    string? Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement PartSha256 = default);
