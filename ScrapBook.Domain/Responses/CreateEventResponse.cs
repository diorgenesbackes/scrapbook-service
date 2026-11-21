namespace ScrapBook.Domain.Responses;

public sealed record CreateEventResponse(EventResponse Event, bool IsReplay);
