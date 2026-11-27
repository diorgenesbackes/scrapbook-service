namespace ScrapBook.Api.Options;

public sealed record MediaProcessingOptions(bool Enabled, string Region, string TopicArn, string QueueUrl,
    string? SnsServiceUrl, string? SqsServiceUrl)
{
    public const string SectionName = "MediaProcessing";

    public static MediaProcessingOptions FromConfiguration(IConfiguration configuration, bool development,
        PhotoSubmissionsOptions photos)
    {
        var options = SnsSqsOptions.FromConfiguration(configuration, SectionName, development);
        if (options.Enabled && (!photos.Enabled || photos.Region != options.Region))
        {
            throw new StartupConfigurationException(SectionName + ":Region");
        }
        return new(options.Enabled, options.Region, options.TopicArn, options.QueueUrl, options.SnsServiceUrl, options.SqsServiceUrl);
    }
}
