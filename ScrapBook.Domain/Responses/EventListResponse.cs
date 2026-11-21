namespace ScrapBook.Domain.Responses;

public sealed record EventListResponse(IReadOnlyList<EventListItemResponse> Items, string? NextCursor);
