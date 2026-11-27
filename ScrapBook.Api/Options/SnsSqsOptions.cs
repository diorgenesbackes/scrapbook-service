namespace ScrapBook.Api.Options;

internal sealed record SnsSqsOptions(bool Enabled, string Region, string TopicArn, string QueueUrl,
    string? SnsServiceUrl, string? SqsServiceUrl)
{
    internal static SnsSqsOptions FromConfiguration(IConfiguration configuration, string sectionName, bool development)
    {
        var section = configuration.GetSection(sectionName);
        if (!bool.TryParse(section["Enabled"] ?? "false", out var enabled))
        {
            throw Invalid(sectionName, "Enabled");
        }
        if (!enabled) return new(false, "", "", "", null, null);

        var region = section["Region"];
        if (region is not { Length: > 0 and <= 32 } || !region.All(character => IsLowerLetter(character) || IsDigit(character) || character == '-'))
        {
            throw Invalid(sectionName, "Region");
        }
        var topic = section["TopicArn"];
        var arn = topic?.Split(':');
        if (arn is not ["arn", ("aws" or "aws-cn" or "aws-us-gov") and var partition, "sns", var arnRegion, var account, var topicName]
            || arnRegion != region || account.Length != 12 || !account.All(IsDigit) || !IsStandardName(topicName))
        {
            throw Invalid(sectionName, "TopicArn");
        }
        var queue = RequireUrl(section["QueueUrl"], development, sectionName, "QueueUrl");
        var queueName = queue.Segments[^1];
        var sqsDomain = partition == "aws-cn" ? "amazonaws.com.cn" : "amazonaws.com";
        if (!IsStandardName(queueName) || (!development && (!queue.IsDefaultPort
            || queue.Host != $"sqs.{region}.{sqsDomain}" || queue.AbsolutePath != $"/{account}/{queueName}")))
        {
            throw Invalid(sectionName, "QueueUrl");
        }
        return new(true, region, topic!, queue.AbsoluteUri, ReadEndpoint(section, development, sectionName, "SnsServiceUrl"),
            ReadEndpoint(section, development, sectionName, "SqsServiceUrl"));
    }

    internal static string? ReadEndpoint(IConfiguration section, bool development, string sectionName, string name)
    {
        var value = section[name];
        if (value is null) return null;
        if (!development) throw Invalid(sectionName, name);
        return RequireUrl(value, true, sectionName, name).AbsoluteUri;
    }

    internal static Uri RequireUrl(string? value, bool development, string sectionName, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.Scheme != Uri.UriSchemeHttps && (!development || uri.Scheme != Uri.UriSchemeHttp))
        {
            throw Invalid(sectionName, name);
        }
        return uri;
    }

    internal static bool IsStandardName(string name) => name.Length is > 0 and <= 80
        && name.All(character => IsLowerLetter(character) || character is >= 'A' and <= 'Z' or '_' or '-' || IsDigit(character));

    private static bool IsLowerLetter(char character) => character is >= 'a' and <= 'z';
    private static bool IsDigit(char character) => character is >= '0' and <= '9';
    private static StartupConfigurationException Invalid(string sectionName, string option) => new(sectionName + ":" + option);
}
