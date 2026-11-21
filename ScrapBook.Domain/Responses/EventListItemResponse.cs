namespace ScrapBook.Domain.Responses;

public sealed record EventListItemResponse(string Id, string Name, string StartsAt, EventCoverResponse Cover);
