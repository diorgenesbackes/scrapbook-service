namespace ScrapBook.Domain.Requests;

public sealed record PhotoSubmissionFileRequest(Guid FileKey, int Position, string? DeclaredMediaType, int SizeBytes, string? Sha256);
