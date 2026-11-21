using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Requests;

public sealed record ReplaceEventOpeningRequest(
    string? Mode, [property: JsonRequired] string? OpensOn, string? ExpectedVersion);
