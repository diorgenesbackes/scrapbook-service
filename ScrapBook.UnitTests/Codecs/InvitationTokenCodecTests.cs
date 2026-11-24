using System.Security.Cryptography;
using System.Text;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Codecs;

namespace ScrapBook.UnitTests.Codecs;

public sealed class InvitationTokenCodecTests
{
    [Fact]
    public void RecoversAnIssuedTokenAndAuthenticatesItsEventAndRequest()
    {
        var hashKey = new byte[32];
        byte[] cipherKey = [.. Enumerable.Repeat((byte)1, 32)];
        var codec = new InvitationTokenCodec("hash-test", hashKey, "cipher-test",
            new Dictionary<string, byte[]> { ["cipher-test"] = cipherKey });
        var request = Guid.NewGuid();
        var issued = codec.Create(42, request);
        var persisted = Stored(42, request, issued);
        var token = codec.Recover(persisted);
        Assert.Matches(@"^v1\.hash-test\.[A-Za-z0-9_-]{43}$", token);
        Assert.Equal(HMACSHA256.HashData(hashKey, Encoding.ASCII.GetBytes(token)), issued.Hash);
        Assert.NotEqual(codec.Create(42, request).Ciphertext, issued.Ciphertext);
        Assert.Throws<InvitationProtectionUnavailableException>(() => codec.Recover(Stored(43, request, issued)));
        Assert.Throws<InvitationProtectionUnavailableException>(() => codec.Recover(Stored(42, Guid.NewGuid(), issued)));
        InvitationTokenCodec otherEnvironment = new("hash-test", new byte[32], "cipher-test",
            new Dictionary<string, byte[]> { ["cipher-test"] = new byte[32] });
        Assert.Throws<InvitationProtectionUnavailableException>(() => otherEnvironment.Recover(persisted));
        issued.Ciphertext[^1] ^= 1;
        Assert.Throws<InvitationProtectionUnavailableException>(() => codec.Recover(persisted));
    }

    private static InvitationEntity Stored(long eventId, Guid request, InvitationSecret issued) => new()
    {
        EventId = eventId,
        RequestKey = request,
        State = "pending",
        TokenHash = issued.Hash,
        HashKeyId = issued.HashKeyId,
        TokenCiphertext = issued.Ciphertext,
        EncryptionKeyId = issued.EncryptionKeyId
    };
}
