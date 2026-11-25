namespace ScrapBook.Api.Options;

public sealed record SubmissionProtectionOptions(string? KeyId, IReadOnlyDictionary<string, byte[]> Keys)
{
    private const string Section = "Submissions";

    public static SubmissionProtectionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!bool.TryParse(section["Enabled"] ?? "false", out var enabled))
        {
            throw new StartupConfigurationException(Section);
        }
        if (!enabled)
        {
            return new(null, keys);
        }

        var id = ParseId(section["KeyId"]);
        keys.Add(id, ParseKey(section["Key"]));
        foreach (var entry in section.GetSection("PreviousKeys").GetChildren())
        {
            if (!keys.TryAdd(ParseId(entry.Key), ParseKey(entry.Value)))
            {
                throw new StartupConfigurationException(Section);
            }
        }
        return new(id, keys);
    }

    private static string ParseId(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
        ? value : throw new StartupConfigurationException(Section);

    private static byte[] ParseKey(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit)
        ? Convert.FromHexString(value) : throw new StartupConfigurationException(Section);
}
