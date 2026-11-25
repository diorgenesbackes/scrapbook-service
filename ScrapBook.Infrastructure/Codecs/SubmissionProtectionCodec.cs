using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Infrastructure.Codecs;

public sealed class SubmissionProtectionCodec(string? activeKeyId, IReadOnlyDictionary<string, byte[]> keys) : ISubmissionProtectionCodec
{
    public string? ActiveKeyId => activeKeyId;

    public string CreateConsent(string keyId, long eventId, long noticeId, string guestName, DateTimeOffset issuedAt)
    {
        var ticks = issuedAt.UtcTicks.ToString(CultureInfo.InvariantCulture);
        var hash = Hash(keyId, "submission-consent-v1", eventId, noticeId, guestName, ticks)!;
        return string.Join('.', keyId, ticks, Convert.ToHexString(hash));
    }

    public DateTimeOffset? VerifyConsent(string proof, long eventId, long noticeId, string guestName)
    {
        var parts = proof.Split('.');
        if (parts.Length != 3 || parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks <= 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return null;
        }

        var expected = Hash(parts[0], "submission-consent-v1", eventId, noticeId, guestName, parts[1]);
        return expected is not null && CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(parts[2]))
            ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    public byte[]? HashPayload(string keyId, long eventId, string noticeCode, string guestName, string text) =>
        Hash(keyId, "submission-payload-v1", eventId, "testimonial", "text", noticeCode, guestName, text);

    public byte[]? HashAuthorization(string keyId, long eventId, Guid attemptKey, ReadOnlySpan<byte> secret) =>
        Hash(keyId, "submission-authorization-v1", eventId, attemptKey, Convert.ToHexString(secret));

    private byte[]? Hash(string keyId, params object[] fields) => keys.TryGetValue(keyId, out var key)
        ? HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(fields))
        : null;
}
