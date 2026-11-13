namespace ScrapBook.Domain.Responses;

public sealed record EventPageResponse(IReadOnlyList<EventListItemResponse> Items, string? NextCursor, bool HasMore, string ServerTime);
