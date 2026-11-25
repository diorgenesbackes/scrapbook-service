namespace ScrapBook.Domain.Requests;

public sealed record CreateSubmissionRequest(string? Category, string? Format, SubmissionIdentificationRequest? Identification, string? Text);
