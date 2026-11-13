using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEventAccessRequest
{
    [JsonRequired] public string OpeningMode { get; init; } = "";
    [JsonRequired] public string? OpeningDate { get; init; }
    [JsonRequired] public string ExpectedVersion { get; init; } = "";
}
