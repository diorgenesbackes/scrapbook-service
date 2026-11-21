using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Api.Codecs;

public sealed class EventCursorCodec(IDataProtectionProvider provider) : IEventCursorCodec
{
    private readonly IDataProtector protector = provider.CreateProtector("ScrapBook.Events.IdDescending.v1");

    public string Encode(long accountId, long beforeId)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(payload, accountId);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8), beforeId);
        return WebEncoders.Base64UrlEncode(protector.Protect(payload));
    }

    public long? Decode(string cursor, long accountId)
    {
        if (cursor.Length is 0 or > 512)
        {
            return null;
        }

        try
        {
            var payload = protector.Unprotect(WebEncoders.Base64UrlDecode(cursor));
            if (payload.Length != 16 || BinaryPrimitives.ReadInt64BigEndian(payload) != accountId)
            {
                return null;
            }

            var id = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(8));
            return id > 0 ? id : null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
