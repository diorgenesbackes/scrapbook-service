namespace ScrapBook.Domain.Responses;

public sealed record EventListItemResponse(string Id, string Name, string StartsAt, CoverResponse Cover, string OpeningMode,
    string? OpeningDate, string Version);
