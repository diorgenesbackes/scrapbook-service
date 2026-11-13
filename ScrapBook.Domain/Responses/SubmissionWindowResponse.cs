namespace ScrapBook.Domain.Responses;

public sealed record SubmissionWindowResponse(string OpensAt, string ClosesAt, string CompletionDeadlineAt);
