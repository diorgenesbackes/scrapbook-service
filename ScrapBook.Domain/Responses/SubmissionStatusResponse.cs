namespace ScrapBook.Domain.Responses;

public sealed record SubmissionStatusResponse(string State, DateTimeOffset ConfirmedAt);
