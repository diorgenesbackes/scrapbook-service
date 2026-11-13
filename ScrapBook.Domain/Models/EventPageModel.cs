namespace ScrapBook.Domain.Models;

public sealed record EventPageModel(IReadOnlyList<EventListItemModel> Items, string? NextCursor, bool HasMore, DateTimeOffset ServerTime);
