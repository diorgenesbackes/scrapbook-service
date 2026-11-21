namespace ScrapBook.Domain.Responses;

public sealed record EventOpeningStatusResponse(
    string ContentState, string? OpensOn, DateTimeOffset? OpensAt, DateTimeOffset ServerTime);
