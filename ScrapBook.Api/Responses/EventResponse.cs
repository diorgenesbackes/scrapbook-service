using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Responses;

public sealed record EventResponse(string Id, string Name, string StartsAt, string TimeZone, string? WelcomeMessage,
    string EffectiveWelcomeMessage, CoverResponse Cover, string OpeningMode, string? OpeningDate, string? OpensAt,
    SubmissionWindowResponse SubmissionWindow, string AccessState, bool CanEditStructure, bool CanEditAccess,
    string Version, string ServerTime);
