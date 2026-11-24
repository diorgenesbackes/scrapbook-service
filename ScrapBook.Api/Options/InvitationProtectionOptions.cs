namespace ScrapBook.Api.Options;

public sealed record InvitationProtectionOptions(
    string? HashKeyId, IReadOnlyDictionary<string, byte[]> HashKeys,
    string? EncryptionKeyId, IReadOnlyDictionary<string, byte[]> EncryptionKeys)
{
    private const string Section = "InvitationProtection";

    public static InvitationProtectionOptions FromConfiguration(IConfiguration configuration)
    {
        var settings = configuration.GetSection(Section);
        var hashId = settings["HashKeyId"];
        var encryptionId = settings["EncryptionKeyId"];
        var hashKey = settings["HashKey"];
        var encryptionKey = settings["EncryptionKey"];
        var previousHash = settings.GetSection("PreviousHashKeys").GetChildren().ToArray();
        var previousEncryption = settings.GetSection("PreviousEncryptionKeys").GetChildren().ToArray();
        if (new[] { hashId, encryptionId, hashKey, encryptionKey }.All(string.IsNullOrEmpty)
            && previousHash.Length == 0 && previousEncryption.Length == 0)
        {
            return new InvitationProtectionOptions(null, new Dictionary<string, byte[]>(),
                null, new Dictionary<string, byte[]>());
        }

        var activeHashId = ReadId(hashId);
        var activeEncryptionId = ReadId(encryptionId);
        var hashes = ReadKeys(activeHashId, hashKey, previousHash);
        var ciphers = ReadKeys(activeEncryptionId, encryptionKey, previousEncryption);
        if (hashes.Values.Any(hash => ciphers.Values.Any(cipher => cipher.AsSpan().SequenceEqual(hash))))
        {
            throw new StartupConfigurationException(Section);
        }

        return new InvitationProtectionOptions(activeHashId, hashes, activeEncryptionId, ciphers);
    }

    private static Dictionary<string, byte[]> ReadKeys(string active, string? value, IConfigurationSection[] previous)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [active] = ReadKey(value) };
        foreach (var entry in previous)
        {
            if (!keys.TryAdd(ReadId(entry.Key), ReadKey(entry.Value)))
            {
                throw new StartupConfigurationException(Section);
            }
        }

        return keys;
    }

    private static string ReadId(string? value)
    {
        if (value is not { Length: > 0 and <= 64 } ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new StartupConfigurationException(Section);
        }

        return value;
    }

    private static byte[] ReadKey(string? value)
    {
        if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
        {
            throw new StartupConfigurationException(Section);
        }

        return Convert.FromHexString(value);
    }
}
