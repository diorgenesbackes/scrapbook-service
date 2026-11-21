namespace ScrapBook.Domain.Requests;

public sealed record CreateEventRequest(string? Name, string? StartsAt, CreateEventOpeningRequest? OpeningPolicy,
    string? WelcomeMessage = null);
