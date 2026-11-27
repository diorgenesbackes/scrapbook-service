namespace ScrapBook.Api.Options;

public sealed record EventStructuralLockOptions(bool Enabled, string Region, string TopicArn, string QueueUrl,
    string? SnsServiceUrl, string? SqsServiceUrl)
{
    public const string SectionName = "EventStructuralLock";

    public static EventStructuralLockOptions FromConfiguration(IConfiguration configuration, bool development)
    {
        var options = SnsSqsOptions.FromConfiguration(configuration, SectionName, development);
        return new(options.Enabled, options.Region, options.TopicArn, options.QueueUrl, options.SnsServiceUrl, options.SqsServiceUrl);
    }
}
