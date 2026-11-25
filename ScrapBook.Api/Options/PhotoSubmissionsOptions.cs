namespace ScrapBook.Api.Options;

public sealed record PhotoSubmissionsOptions(bool Enabled, string Bucket, string QueueUrl, string Region,
    string? StorageServiceUrl, string? QueueServiceUrl, string? BrowserStorageServiceUrl, bool ForcePathStyle)
{
    private const string Section = "PhotoSubmissions";

    public static PhotoSubmissionsOptions FromConfiguration(IConfiguration configuration, bool development, bool submissionsEnabled)
    {
        var section = configuration.GetSection(Section);
        if (!bool.TryParse(section["Enabled"] ?? "false", out var enabled)) throw new StartupConfigurationException(Section);
        if (!enabled) return new(false, "", "", "", null, null, null, false);
        if (!submissionsEnabled) throw new StartupConfigurationException(Section);
        var bucket = section["Bucket"];
        if (bucket is not { Length: >= 3 and <= 63 } || !IsLetterOrDigit(bucket[0]) || !IsLetterOrDigit(bucket[^1])
            || !bucket.All(character => IsLetterOrDigit(character) || character is '.' or '-'))
        {
            throw new StartupConfigurationException(Section);
        }
        var region = section["Region"];
        if (region is not { Length: > 0 and <= 32 } || !region.All(character => IsLetterOrDigit(character) || character == '-'))
        {
            throw new StartupConfigurationException(Section);
        }
        var queue = RequireUrl(section["QueueUrl"], development);
        var storageEndpoint = ReadEndpoint(section["StorageServiceUrl"], development);
        var queueEndpoint = ReadEndpoint(section["QueueServiceUrl"], development);
        var browserEndpoint = ReadEndpoint(section["BrowserStorageServiceUrl"], development);
        if (!bool.TryParse(section["ForcePathStyle"] ?? "false", out var forcePathStyle) || forcePathStyle && !development)
        {
            throw new StartupConfigurationException(Section);
        }
        return new(true, bucket, queue, region, storageEndpoint, queueEndpoint, browserEndpoint, forcePathStyle);
    }

    private static string? ReadEndpoint(string? value, bool development) => value is null
        ? null : development ? RequireUrl(value, true) : throw new StartupConfigurationException(Section);

    private static string RequireUrl(string? value, bool development)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0 || uri.Scheme != Uri.UriSchemeHttps && (!development || uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new StartupConfigurationException(Section);
        }
        return uri.AbsoluteUri;
    }

    private static bool IsLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
