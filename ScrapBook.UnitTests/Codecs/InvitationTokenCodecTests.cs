using System.Security.Cryptography;
using System.Text;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Codecs;

namespace ScrapBook.UnitTests.Codecs;

public sealed class InvitationTokenCodecTests
{
    [Fact]
    public void ResolvesPreviousHashKeysWithoutAcceptingUnknownOrMalformedTokens()
    {
        var cipherKeys = new Dictionary<string, byte[]> { ["cipher"] = [.. Enumerable.Repeat((byte)1, 32)] };
        var oldHash = new byte[32];
        byte[] newHash = [.. Enumerable.Repeat((byte)2, 32)];
        var old = new InvitationTokenCodec("old", new Dictionary<string, byte[]> { ["old"] = oldHash }, "cipher", cipherKeys);
        var key = Guid.NewGuid();
        var issued = old.Create(42, key);
        var token = old.Recover(Stored(42, key, issued));
        var rotated = new InvitationTokenCodec("new", new Dictionary<string, byte[]> { ["new"] = newHash, ["old"] = oldHash },
            "cipher", cipherKeys);

        var digest = rotated.Digest(token);
        Assert.NotNull(digest);
        Assert.Equal(issued.Hash, digest.Hash);
        Assert.Equal("old", digest.KeyId);
        Assert.Null(rotated.Digest(token.Replace(".old.", ".other.", StringComparison.Ordinal)));
        Assert.Null(rotated.Digest(token + "\n"));
        Assert.Null(rotated.Digest(token[..^1] + "%"));
        var other = new InvitationTokenCodec("old", new Dictionary<string, byte[]> { ["old"] = newHash }, "cipher", cipherKeys);
        Assert.False(issued.Hash.SequenceEqual(other.Digest(token)!.Hash));
    }

    [Fact]
    public void RecoversAnIssuedTokenAndAuthenticatesItsEventAndRequest()
    {
        var hashKey = new byte[32];
        byte[] cipherKey = [.. Enumerable.Repeat((byte)1, 32)];
        var codec = new InvitationTokenCodec("hash-test", new Dictionary<string, byte[]> { ["hash-test"] = hashKey }, "cipher-test",
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
        InvitationTokenCodec otherEnvironment = new("hash-test", new Dictionary<string, byte[]> { ["hash-test"] = new byte[32] }, "cipher-test",
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
