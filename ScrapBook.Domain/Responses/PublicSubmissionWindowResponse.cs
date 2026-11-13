namespace ScrapBook.Domain.Responses;

public sealed record PublicSubmissionWindowResponse(string State, string OpensAt, string ClosesAt, string TimeZone, int RevalidateAfterMs);
