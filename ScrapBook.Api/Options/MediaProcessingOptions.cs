namespace ScrapBook.Api.Options;

public sealed record MediaProcessingOptions(bool Enabled, string Region, string TopicArn, string QueueUrl,
    string? SnsServiceUrl, string? SqsServiceUrl)
{
    public const string SectionName = "MediaProcessing";

    public static MediaProcessingOptions FromConfiguration(IConfiguration configuration, bool development,
        PhotoSubmissionsOptions photos, EventStructuralLockOptions structuralLock)
    {
        var options = SnsSqsOptions.FromConfiguration(configuration, SectionName, development);
        if (options.Enabled && (!photos.Enabled || photos.Region != options.Region))
        {
            throw new StartupConfigurationException(SectionName + ":Region");
        }
        if (options.Enabled && (SameQueue(options.QueueUrl, photos.QueueUrl)
            || structuralLock.Enabled && SameQueue(options.QueueUrl, structuralLock.QueueUrl)))
        {
            throw new StartupConfigurationException(SectionName + ":QueueUrl");
        }
        return new(options.Enabled, options.Region, options.TopicArn, options.QueueUrl, options.SnsServiceUrl, options.SqsServiceUrl);
    }

    private static bool SameQueue(string firstUrl, string secondUrl)
    {
        var first = new Uri(firstUrl, UriKind.Absolute);
        var second = new Uri(secondUrl, UriKind.Absolute);
        return first.Scheme == second.Scheme && first.Port == second.Port
            && string.Equals(first.IdnHost.TrimEnd('.'), second.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && Uri.UnescapeDataString(first.AbsolutePath).TrimEnd('/') == Uri.UnescapeDataString(second.AbsolutePath).TrimEnd('/');
    }
}
