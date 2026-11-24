using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Infrastructure.Codecs;

public sealed class InvitationTokenCodec(
    string? hashKeyId, IReadOnlyDictionary<string, byte[]> hashKeys,
    string? encryptionKeyId, IReadOnlyDictionary<string, byte[]> encryptionKeys)
    : IInvitationTokenCodec
{
    private const int SecretSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public bool IsAvailable => hashKeyId is not null;

    public InvitationSecret Create(long eventId, Guid requestKey)
    {
        if (hashKeyId is null || encryptionKeyId is null)
        {
            throw new InvitationProtectionUnavailableException();
        }

        var secret = RandomNumberGenerator.GetBytes(SecretSize);
        try
        {
            var token = Token(hashKeyId, secret);
            var hash = Hash(hashKeys[hashKeyId], token);
            var envelope = new byte[NonceSize + SecretSize + TagSize];
            var nonce = envelope.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var cipher = new AesGcm(encryptionKeys[encryptionKeyId], TagSize);
            cipher.Encrypt(nonce, secret, envelope.AsSpan(NonceSize, SecretSize), envelope.AsSpan(NonceSize + SecretSize),
                AssociatedData(eventId, requestKey, hashKeyId));
            return new InvitationSecret(hashKeyId, hash, encryptionKeyId, envelope);
        }
        catch (CryptographicException error)
        {
            throw new InvitationProtectionUnavailableException(error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public string Recover(InvitationEntity invitation)
    {
        if (invitation.EncryptionKeyId is null || !encryptionKeys.TryGetValue(invitation.EncryptionKeyId, out var key)
            || invitation.TokenCiphertext is not { Length: NonceSize + SecretSize + TagSize } envelope)
        {
            throw new InvitationProtectionUnavailableException();
        }

        Span<byte> secret = stackalloc byte[SecretSize];
        try
        {
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(envelope.AsSpan(0, NonceSize), envelope.AsSpan(NonceSize, SecretSize),
                envelope.AsSpan(NonceSize + SecretSize), secret,
                AssociatedData(invitation.EventId, invitation.RequestKey, invitation.HashKeyId));
            return Token(invitation.HashKeyId, secret);
        }
        catch (CryptographicException error)
        {
            throw new InvitationProtectionUnavailableException(error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public InvitationDigest? Digest(string token)
    {
        if (token.Length is < 48 or > 111 || !token.StartsWith("v1.", StringComparison.Ordinal))
        {
            return null;
        }

        var separator = token.IndexOf('.', 3);
        if (separator < 4 || token.Length - separator != 44)
        {
            return null;
        }

        var keyId = token[3..separator];
        var secret = token[(separator + 1)..];
        return hashKeys.TryGetValue(keyId, out var key)
            && secret.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_')
            ? new InvitationDigest(keyId, Hash(key, token))
            : null;
    }

    private static byte[] Hash(byte[] key, string token) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(token));

    private static byte[] AssociatedData(long eventId, Guid requestKey, string keyId) =>
        Encoding.ASCII.GetBytes(FormattableString.Invariant($"scrapbook:invitation:v1:{eventId}:{requestKey:D}:{keyId}"));

    private static string Token(string keyId, ReadOnlySpan<byte> secret) => $"v1.{keyId}.{Base64Url.EncodeToString(secret)}";
}
