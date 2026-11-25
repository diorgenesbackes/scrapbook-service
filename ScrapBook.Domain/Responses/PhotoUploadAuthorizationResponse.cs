namespace ScrapBook.Domain.Responses;

public sealed record PhotoUploadAuthorizationResponse(string Method, string Url, IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt);
