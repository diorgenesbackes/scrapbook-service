namespace ScrapBook.Domain.Responses;

public sealed record CreateSubmissionResponse(bool Created, SubmissionStatusResponse Status);
