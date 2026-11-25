namespace ScrapBook.Domain.Requests;

public sealed record SubmissionIdentificationRequest(string? GuestName, string? NoticeCode, string? ConsentProof);
