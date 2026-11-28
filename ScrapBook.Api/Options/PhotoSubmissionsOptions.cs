namespace ScrapBook.Api.Options;

public sealed record PhotoSubmissionsOptions(bool Enabled, string Bucket, string QueueUrl, string Region,
    string? StorageServiceUrl, string? QueueServiceUrl, string? BrowserStorageServiceUrl, bool ForcePathStyle,
    bool VideoEnabled, string VideoProbePath, bool AudioEnabled = false)
{
    private const string Section = "PhotoSubmissions";
    private const string DefaultVideoProbe = "/usr/bin/ffprobe";

    public static PhotoSubmissionsOptions FromConfiguration(IConfiguration configuration, bool development, bool submissionsEnabled)
    {
        var section = configuration.GetSection(Section);
        if (!bool.TryParse(section["Enabled"] ?? "false", out var enabled)) throw new StartupConfigurationException(Section);
        if (!bool.TryParse(section["VideoEnabled"] ?? "false", out var videoEnabled) || videoEnabled && !enabled)
        {
            throw new StartupConfigurationException(Section + ":VideoEnabled");
        }
        if (!bool.TryParse(section["AudioEnabled"] ?? "false", out var audioEnabled) || audioEnabled && !enabled)
        {
            throw new StartupConfigurationException(Section + ":AudioEnabled");
        }
        if (!enabled) return new(false, "", "", "", null, null, null, false, false, DefaultVideoProbe);
        if (!submissionsEnabled) throw new StartupConfigurationException(Section);
        var bucket = RequireBucket(section["Bucket"]);
        var region = RequireRegion(section["Region"]);
        var queue = RequireUrl(section["QueueUrl"], development);
        var storageEndpoint = ReadEndpoint(section["StorageServiceUrl"], development);
        var queueEndpoint = ReadEndpoint(section["QueueServiceUrl"], development);
        var browserEndpoint = ReadEndpoint(section["BrowserStorageServiceUrl"], development);
        if (!bool.TryParse(section["ForcePathStyle"] ?? "false", out var forcePathStyle) || forcePathStyle && !development)
        {
            throw new StartupConfigurationException(Section);
        }
        var videoProbePath = section["VideoProbePath"] ?? DefaultVideoProbe;
        if (!Path.IsPathFullyQualified(videoProbePath) || (videoEnabled || audioEnabled) && !File.Exists(videoProbePath))
        {
            throw new StartupConfigurationException(Section + ":VideoProbePath");
        }
        if (audioEnabled && !File.Exists(Path.Combine(Path.GetDirectoryName(videoProbePath)!, "ffmpeg")))
        {
            throw new StartupConfigurationException(Section + ":VideoProbePath");
        }
        return new(true, bucket, queue, region, storageEndpoint, queueEndpoint, browserEndpoint, forcePathStyle,
            videoEnabled, videoProbePath, audioEnabled);
    }

    internal static string RequireBucket(string? value)
    {
        if (value is not { Length: >= 3 and <= 63 } || !IsLetterOrDigit(value[0]) || !IsLetterOrDigit(value[^1])
            || !value.All(character => IsLetterOrDigit(character) || character is '.' or '-'))
        {
            throw new StartupConfigurationException(Section + ":Bucket");
        }
        return value;
    }

    internal static string RequireRegion(string? value)
    {
        if (value is not { Length: > 0 and <= 32 } || !value.All(character => IsLetterOrDigit(character) || character == '-'))
        {
            throw new StartupConfigurationException(Section + ":Region");
        }
        return value;
    }

    internal static string? ReadEndpoint(string? value, bool development) => value is null
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
