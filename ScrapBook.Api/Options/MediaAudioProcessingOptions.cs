using ScrapBook.Domain.Models;

namespace ScrapBook.Api.Options;

public sealed record MediaAudioProcessingOptions(bool Enabled, MediaAudioSettings Settings, string ResultQueueUrl,
    string? MediaConvertServiceUrl, string? SqsServiceUrl)
{
    public const string SectionName = "MediaAudioProcessing";

    public static MediaAudioProcessingOptions FromConfiguration(IConfiguration configuration, bool development,
        PhotoSubmissionsOptions photos, MediaProcessingOptions media, EventStructuralLockOptions structuralLock)
    {
        var section = configuration.GetSection(SectionName);
        if (!bool.TryParse(section["Enabled"] ?? "false", out var enabled)) throw Invalid("Enabled");
        if (!enabled) return new(false, new("", "", "", ""), "", null, null);
        if (!photos.Enabled || !photos.AudioEnabled || !media.Enabled) throw Invalid("Enabled");

        if (section["Region"] is not { Length: > 0 and <= 32 } region || region != photos.Region || region != media.Region)
        {
            throw Invalid("Region");
        }
        var queueArn = section["QueueArn"];
        if (queueArn?.Split(':') is not ["arn", ("aws" or "aws-cn" or "aws-us-gov") and var partition, "mediaconvert",
            var queueRegion, { Length: 12 } and var account, var queueResource] || queueRegion != region
            || !account.All(char.IsAsciiDigit) || !queueResource.StartsWith("queues/", StringComparison.Ordinal)
            || !SnsSqsOptions.IsStandardName(queueResource["queues/".Length..]))
        {
            throw Invalid("QueueArn");
        }

        var jobRoleArn = section["JobRoleArn"];
        var rolePrefix = $"arn:{partition}:iam::{account}:role/";
        if (jobRoleArn is null || !jobRoleArn.StartsWith(rolePrefix, StringComparison.Ordinal)
            || !ValidRolePath(jobRoleArn[rolePrefix.Length..]))
        {
            throw Invalid("JobRoleArn");
        }
        var queue = SnsSqsOptions.RequireUrl(section["ResultQueueUrl"], development, SectionName, "ResultQueueUrl");
        var queueName = queue.Segments[^1];
        var domain = partition == "aws-cn" ? "amazonaws.com.cn" : "amazonaws.com";
        if (!SnsSqsOptions.IsStandardName(queueName)
            || !development && (!queue.IsDefaultPort || queue.Host != $"sqs.{region}.{domain}"
                || queue.AbsolutePath != $"/{account}/{queueName}")
            || MediaProcessingOptions.SameQueue(queue.AbsoluteUri, photos.QueueUrl)
            || MediaProcessingOptions.SameQueue(queue.AbsoluteUri, media.QueueUrl)
            || structuralLock.Enabled && MediaProcessingOptions.SameQueue(queue.AbsoluteUri, structuralLock.QueueUrl))
        {
            throw Invalid("ResultQueueUrl");
        }
        var settings = new MediaAudioSettings(account, region, queueArn, jobRoleArn);
        return new(true, settings, queue.AbsoluteUri,
            SnsSqsOptions.ReadEndpoint(section, development, SectionName, "MediaConvertServiceUrl"),
            SnsSqsOptions.ReadEndpoint(section, development, SectionName, "SqsServiceUrl"));
    }

    private static bool ValidRolePath(string value) =>
        value.Length is > 0 and <= 512 && value.Split('/').All(part => part.Length > 0
            && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '+' or '=' or ',' or '.' or '@' or '-'));

    private static StartupConfigurationException Invalid(string key) => new(SectionName + ":" + key);
}
