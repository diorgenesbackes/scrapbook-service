namespace ScrapBook.Application.Models;

public sealed record EventListItemModel(long Id, string Name, DateTime StartsAt, string OpeningMode,
    DateTime? OpensAt, long Version, DateTimeOffset CreatedAt);
