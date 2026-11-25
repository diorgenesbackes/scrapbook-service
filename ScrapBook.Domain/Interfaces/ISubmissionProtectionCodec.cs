namespace ScrapBook.Domain.Interfaces;

public interface ISubmissionProtectionCodec
{
    string? ActiveKeyId { get; }
    string CreateConsent(string keyId, long eventId, long noticeId, string guestName, DateTimeOffset issuedAt);
    DateTimeOffset? VerifyConsent(string proof, long eventId, long noticeId, string guestName);
    byte[]? HashPayload(string keyId, long eventId, string noticeCode, string guestName, string text);
    byte[]? HashAuthorization(string keyId, long eventId, Guid attemptKey, ReadOnlySpan<byte> secret);
}
