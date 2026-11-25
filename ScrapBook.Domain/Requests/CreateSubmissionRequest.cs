namespace ScrapBook.Domain.Requests;

public sealed record CreateSubmissionRequest(string? Category, string? Format, SubmissionIdentificationRequest? Identification,
    string? Text, string? Caption = null, IReadOnlyList<PhotoSubmissionFileRequest?>? Files = null);
