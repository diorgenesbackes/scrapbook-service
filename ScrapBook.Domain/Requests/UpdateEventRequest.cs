using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEventRequest
{
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public string StartsAt { get; init; } = "";
    [JsonRequired] public string? WelcomeMessage { get; init; }
    [JsonRequired] public string ExpectedVersion { get; init; } = "";
}
