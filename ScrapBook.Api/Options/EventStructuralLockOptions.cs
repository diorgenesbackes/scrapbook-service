namespace ScrapBook.Api.Options;

public sealed record EventStructuralLockOptions(bool Enabled, string Region, string TopicArn, string QueueUrl,
    string? SnsServiceUrl, string? SqsServiceUrl)
{
    public const string SectionName = "EventStructuralLock";

    public static EventStructuralLockOptions FromConfiguration(IConfiguration configuration, bool development)
    {
        var section = configuration.GetSection(SectionName);
        if (!bool.TryParse(section["Enabled"] ?? "false", out var enabled))
        {
            throw Invalid("Enabled");
        }
        if (!enabled) return new(false, "", "", "", null, null);

        var region = section["Region"];
        if (region is not { Length: > 0 and <= 32 } || !region.All(character => IsLowerLetter(character) || IsDigit(character) || character == '-'))
        {
            throw Invalid("Region");
        }
        var topic = section["TopicArn"];
        var arn = topic?.Split(':');
        if (arn is not ["arn", "aws" or "aws-cn" or "aws-us-gov", "sns", var arnRegion, var account, var topicName]
            || arnRegion != region || account.Length != 12 || !account.All(IsDigit) || !IsStandardName(topicName))
        {
            throw Invalid("TopicArn");
        }
        var queue = RequireUrl(section["QueueUrl"], development, "QueueUrl");
        if (!IsStandardName(queue.Segments[^1]))
        {
            throw Invalid("QueueUrl");
        }
        return new(true, region, topic!, queue.AbsoluteUri, ReadEndpoint(section, development, "SnsServiceUrl"),
            ReadEndpoint(section, development, "SqsServiceUrl"));
    }

    private static string? ReadEndpoint(IConfiguration section, bool development, string name)
    {
        var value = section[name];
        if (value is null) return null;
        if (!development) throw Invalid(name);
        return RequireUrl(value, true, name).AbsoluteUri;
    }

    private static Uri RequireUrl(string? value, bool development, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.Scheme != Uri.UriSchemeHttps && (!development || uri.Scheme != Uri.UriSchemeHttp))
        {
            throw Invalid(name);
        }
        return uri;
    }

    private static bool IsStandardName(string name) => name.Length is > 0 and <= 80
        && name.All(character => IsLowerLetter(character) || character is >= 'A' and <= 'Z' or '_' or '-' || IsDigit(character));

    private static bool IsLowerLetter(char character) => character is >= 'a' and <= 'z';
    private static bool IsDigit(char character) => character is >= '0' and <= '9';
    private static StartupConfigurationException Invalid(string option) => new(SectionName + ":" + option);
}
