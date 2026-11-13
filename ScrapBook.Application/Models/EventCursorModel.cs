namespace ScrapBook.Application.Models;

public sealed record EventCursorModel(long AccountId, DateTimeOffset CreatedAt, long EventId);
