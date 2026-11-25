namespace ScrapBook.Domain.Requests;

public sealed record ConfirmSubmissionConsentRequest(string? GuestName, string? NoticeCode, bool Accepted);
