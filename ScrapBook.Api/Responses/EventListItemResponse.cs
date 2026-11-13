using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Responses;

public sealed record EventListItemResponse(string Id, string Name, string StartsAt, CoverResponse Cover, string OpeningMode,
    string? OpeningDate, string Version);
