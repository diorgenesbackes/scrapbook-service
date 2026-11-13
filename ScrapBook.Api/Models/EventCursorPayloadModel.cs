namespace ScrapBook.Api.Models;

internal sealed record EventCursorPayloadModel(int V, string Order, long AccountId, DateTimeOffset CreatedAt, long EventId);
