using Microsoft.Extensions.Configuration;
using ScrapBook.Api.Options;

namespace ScrapBook.UnitTests.Options;

public sealed class MediaProcessingOptionsTests
{
    private const string Region = "us-east-1";
    private const string Topic = "arn:aws:sns:us-east-1:123456789012:events";
    private const string MediaQueue = "https://sqs.us-east-1.amazonaws.com/123456789012/media-confirmation";
    private const string ReceiptQueue = "https://sqs.us-east-1.amazonaws.com/123456789012/receipts";
    [Fact]
    public void StartsDisabledIndependentlyOfStructuralLockConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EventStructuralLock:Enabled"] = "true"
        }).Build();

        var options = MediaProcessingOptions.FromConfiguration(configuration, false, Photos(true, Region), StructuralLock(false));

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData(false, "us-east-1")]
    [InlineData(true, "us-west-2")]
    public void RequiresEnabledPhotoIngressInTheSameRegion(bool photoEnabled, string photoRegion)
    {
        var error = Assert.Throws<StartupConfigurationException>(() =>
            MediaProcessingOptions.FromConfiguration(Enabled(), false, Photos(photoEnabled, photoRegion), StructuralLock(false)));

        Assert.Equal("MediaProcessing:Region", error.OptionName);
    }

    [Fact]
    public void UsesTheMediaQueueWithoutRequiringStructuralLockOptions()
    {
        var options = MediaProcessingOptions.FromConfiguration(Enabled(), false, Photos(true, Region), StructuralLock(false));

        Assert.True(options.Enabled);
        Assert.False(options.Paused);
        Assert.Equal(MediaQueue, options.QueueUrl);
    }

    [Theory]
    [InlineData("true", "true", true, true)]
    [InlineData("true", "false", true, false)]
    [InlineData("false", "true", false, true)]
    public void ReadsTheAcquisitionPauseIndependentlyOfFeatureEnablement(string enabledValue, string pausedValue,
        bool enabled, bool paused)
    {
        var configuration = Enabled();
        configuration["MediaProcessing:Enabled"] = enabledValue;
        configuration["MediaProcessing:Paused"] = pausedValue;

        var options = MediaProcessingOptions.FromConfiguration(configuration, false, Photos(true, Region), StructuralLock(false));

        Assert.Equal(enabled, options.Enabled);
        Assert.Equal(paused, options.Paused);
    }

    [Theory]
    [InlineData("true", "1")]
    [InlineData("false", "enable")]
    [InlineData("true", "")]
    public void RejectsAnInvalidAcquisitionPauseAtStartup(string enabled, string pause)
    {
        var configuration = Enabled();
        configuration["MediaProcessing:Enabled"] = enabled;
        configuration["MediaProcessing:Paused"] = pause;

        var error = Assert.Throws<StartupConfigurationException>(() => MediaProcessingOptions.FromConfiguration(
            configuration, false, Photos(true, Region), StructuralLock(false)));

        Assert.Equal("MediaProcessing:Paused", error.OptionName);
    }

    [Theory]
    [InlineData(MediaQueue)]
    [InlineData("https://SQS.US-EAST-1.AMAZONAWS.COM:443/123456789012/%6Dedia-confirmation/")]
    public void RejectsThePhotoReceiptQueueEvenWhenTheUrlUsesEquivalentSpelling(string receiptQueue)
    {
        var error = Assert.Throws<StartupConfigurationException>(() => MediaProcessingOptions.FromConfiguration(
            Enabled(), false, Photos(true, Region, receiptQueue), StructuralLock(false)));

        Assert.Equal("MediaProcessing:QueueUrl", error.OptionName);
    }

    [Fact]
    public void RejectsTheEnabledStructuralLockQueueEvenWhenTheHostAndPortUseEquivalentSpelling()
    {
        const string queue = "https://SQS.US-EAST-1.AMAZONAWS.COM:443/123456789012/media-confirmation";
        var error = Assert.Throws<StartupConfigurationException>(() => MediaProcessingOptions.FromConfiguration(
            Enabled(), false, Photos(true, Region), StructuralLock(true, queue)));

        Assert.Equal("MediaProcessing:QueueUrl", error.OptionName);
    }

    [Fact]
    public void AllowsAQueueUrlTheDisabledStructuralLockConsumerDoesNotUse()
    {
        var options = MediaProcessingOptions.FromConfiguration(Enabled(), false, Photos(true, Region),
            StructuralLock(false, MediaQueue));

        Assert.True(options.Enabled);
        Assert.Equal(MediaQueue, options.QueueUrl);
    }

    [Fact]
    public void AllowsTheEnabledConsumersToShareATopicAndUseDistinctQueues()
    {
        var structuralLock = StructuralLock(true, "https://sqs.us-east-1.amazonaws.com/123456789012/Media-confirmation");
        var options = MediaProcessingOptions.FromConfiguration(Enabled(), false, Photos(true, Region), structuralLock);

        Assert.True(options.Enabled);
        Assert.Equal(structuralLock.TopicArn, options.TopicArn);
        Assert.NotEqual(structuralLock.QueueUrl, options.QueueUrl);
    }

    private static IConfiguration Enabled() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MediaProcessing:Enabled"] = "true",
        ["MediaProcessing:Region"] = Region,
        ["MediaProcessing:TopicArn"] = Topic,
        ["MediaProcessing:QueueUrl"] = MediaQueue
    }).Build();

    private static EventStructuralLockOptions StructuralLock(bool enabled, string queue = "") =>
        new(enabled, Region, Topic, queue, null, null);

    private static PhotoSubmissionsOptions Photos(bool enabled, string region, string queue = ReceiptQueue) =>
        new(enabled, "private-media", queue, region,
            null, null, null, false, false, "/usr/bin/ffprobe");
}
