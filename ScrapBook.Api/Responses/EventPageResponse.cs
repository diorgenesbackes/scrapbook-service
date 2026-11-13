using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Responses;

public sealed record EventPageResponse(IReadOnlyList<EventListItemResponse> Items, string? NextCursor, bool HasMore, string ServerTime);
