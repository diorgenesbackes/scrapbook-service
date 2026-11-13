namespace ScrapBook.Domain.Models;

public sealed record EventCursorPayloadModel(int V, string Order, long AccountId, DateTimeOffset CreatedAt, long EventId);
