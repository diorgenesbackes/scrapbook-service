namespace ScrapBook.Application.Commands;

public sealed record UpdateEventCommand(string? Name, DateTime StartsAt, string? WelcomeMessage, long ExpectedVersion);
