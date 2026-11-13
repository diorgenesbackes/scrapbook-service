using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEventRequest
{
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public string StartsAt { get; init; } = "";
    [JsonRequired] public string? WelcomeMessage { get; init; }
    [JsonRequired] public string ExpectedVersion { get; init; } = "";
}
