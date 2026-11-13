namespace ScrapBook.Application.Commands;

public sealed record UpdateEventAccessCommand(string OpeningMode, DateOnly? OpeningDate, long ExpectedVersion);
