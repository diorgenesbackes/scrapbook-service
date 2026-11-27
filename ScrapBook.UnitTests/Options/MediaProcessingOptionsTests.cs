using Microsoft.Extensions.Configuration;
using ScrapBook.Api.Options;

namespace ScrapBook.UnitTests.Options;

public sealed class MediaProcessingOptionsTests
{
    [Fact]
    public void StartsDisabledIndependentlyOfStructuralLockConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EventStructuralLock:Enabled"] = "true"
        }).Build();

        var options = MediaProcessingOptions.FromConfiguration(configuration, false, Photos(true, "us-east-1"));

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData(false, "us-east-1")]
    [InlineData(true, "us-west-2")]
    public void RequiresEnabledPhotoIngressInTheSameRegion(bool photoEnabled, string photoRegion)
    {
        var error = Assert.Throws<StartupConfigurationException>(() =>
            MediaProcessingOptions.FromConfiguration(Enabled(), false, Photos(photoEnabled, photoRegion)));

        Assert.Equal("MediaProcessing:Region", error.OptionName);
    }

    [Fact]
    public void UsesTheMediaQueueWithoutRequiringStructuralLockOptions()
    {
        var options = MediaProcessingOptions.FromConfiguration(Enabled(), false, Photos(true, "us-east-1"));

        Assert.True(options.Enabled);
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/123456789012/media-confirmation", options.QueueUrl);
    }

    private static IConfiguration Enabled() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MediaProcessing:Enabled"] = "true",
        ["MediaProcessing:Region"] = "us-east-1",
        ["MediaProcessing:TopicArn"] = "arn:aws:sns:us-east-1:123456789012:events",
        ["MediaProcessing:QueueUrl"] = "https://sqs.us-east-1.amazonaws.com/123456789012/media-confirmation"
    }).Build();

    private static PhotoSubmissionsOptions Photos(bool enabled, string region) =>
        new(enabled, "private-media", "https://sqs.us-east-1.amazonaws.com/123456789012/receipts", region,
            null, null, null, false, false, "/usr/bin/ffprobe");
}
