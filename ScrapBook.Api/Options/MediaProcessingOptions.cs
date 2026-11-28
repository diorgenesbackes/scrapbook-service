namespace ScrapBook.Api.Options;

public sealed record MediaProcessingOptions(bool Enabled, string Region, string TopicArn, string QueueUrl,
    string? SnsServiceUrl, string? SqsServiceUrl, DateTimeOffset? HistoricalBefore = null, bool Paused = false)
{
    public const string SectionName = "MediaProcessing";

    public static MediaProcessingOptions FromConfiguration(IConfiguration configuration, bool development,
        PhotoSubmissionsOptions photos, EventStructuralLockOptions structuralLock)
    {
        var paused = ReadPaused(configuration);
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
        return new(options.Enabled, options.Region, options.TopicArn, options.QueueUrl, options.SnsServiceUrl, options.SqsServiceUrl,
            options.Enabled ? MediaHistoryOptions.ReadCutoff(configuration) : null, paused);
    }

    internal static bool ReadPaused(IConfiguration configuration)
    {
        const string key = SectionName + ":Paused";
        return bool.TryParse(configuration[key] ?? "false", out var paused)
            ? paused : throw new StartupConfigurationException(key);
    }

    internal static bool SameQueue(string firstUrl, string secondUrl)
    {
        var first = new Uri(firstUrl, UriKind.Absolute);
        var second = new Uri(secondUrl, UriKind.Absolute);
        return first.Scheme == second.Scheme && first.Port == second.Port
            && string.Equals(first.IdnHost.TrimEnd('.'), second.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && Uri.UnescapeDataString(first.AbsolutePath).TrimEnd('/') == Uri.UnescapeDataString(second.AbsolutePath).TrimEnd('/');
    }
}
