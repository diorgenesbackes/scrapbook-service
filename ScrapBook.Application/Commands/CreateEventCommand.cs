namespace ScrapBook.Application.Commands;

public sealed record CreateEventCommand(string? Name, DateTime StartsAt, string? WelcomeMessage,
    string OpeningMode = "scheduled", DateOnly? OpeningDate = null);
