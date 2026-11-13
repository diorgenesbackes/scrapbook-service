using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEventAccessRequest
{
    [JsonRequired] public string OpeningMode { get; init; } = "";
    [JsonRequired] public string? OpeningDate { get; init; }
    [JsonRequired] public string ExpectedVersion { get; init; } = "";
}
