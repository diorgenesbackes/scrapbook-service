namespace ScrapBook.Api.Options;

public sealed record InvitationProtectionOptions(
    string? HashKeyId, byte[]? HashKey, string? EncryptionKeyId, IReadOnlyDictionary<string, byte[]> EncryptionKeys)
{
    private const string Section = "InvitationProtection";

    public static InvitationProtectionOptions FromConfiguration(IConfiguration configuration)
    {
        var settings = configuration.GetSection(Section);
        var hashId = settings["HashKeyId"];
        var encryptionId = settings["EncryptionKeyId"];
        var hashKey = settings["HashKey"];
        var encryptionKey = settings["EncryptionKey"];
        var previousKeys = settings.GetSection("PreviousEncryptionKeys").GetChildren().ToArray();
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (new[] { hashId, encryptionId, hashKey, encryptionKey }.All(string.IsNullOrEmpty) && previousKeys.Length == 0)
        {
            return new InvitationProtectionOptions(null, null, null, keys);
        }

        var activeHashId = ReadId(hashId);
        var activeEncryptionId = ReadId(encryptionId);
        var activeHashKey = ReadKey(hashKey);
        var activeEncryptionKey = ReadKey(encryptionKey);
        keys.Add(activeEncryptionId, activeEncryptionKey);
        foreach (var previous in previousKeys)
        {
            if (!keys.TryAdd(ReadId(previous.Key), ReadKey(previous.Value)))
            {
                throw new StartupConfigurationException(Section);
            }
        }
        if (keys.Values.Any(key => key.AsSpan().SequenceEqual(activeHashKey)))
        {
            throw new StartupConfigurationException(Section);
        }

        return new InvitationProtectionOptions(activeHashId, activeHashKey, activeEncryptionId, keys);
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
