using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Requests;

public sealed record ReplaceEventDetailsRequest(
    string? Name, string? StartsAt, [property: JsonRequired] string? WelcomeMessage, string? ExpectedVersion);
