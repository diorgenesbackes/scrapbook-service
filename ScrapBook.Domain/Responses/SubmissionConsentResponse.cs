namespace ScrapBook.Domain.Responses;

public sealed record SubmissionConsentResponse(string ConsentProof, DateTimeOffset ExpiresAt);
