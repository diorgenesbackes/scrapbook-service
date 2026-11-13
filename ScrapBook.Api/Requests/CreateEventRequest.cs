using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateEventRequest
{
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public string StartsAt { get; init; } = "";
    public string? WelcomeMessage { get; init; }
    public string OpeningMode { get; init; } = "scheduled";
    public string? OpeningDate { get; init; }
}
