using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Responses;

public sealed record MediaUploadStatusResponse(string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PartSizeBytes, IReadOnlyList<int> UploadedParts, DateTimeOffset CompletionDeadlineAt);
