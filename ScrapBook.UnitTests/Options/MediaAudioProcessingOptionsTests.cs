using Microsoft.Extensions.Configuration;
using ScrapBook.Api.Options;
using static ScrapBook.UnitTests.Services.MediaAudioTestFixture;

namespace ScrapBook.UnitTests.Options;

public sealed class MediaAudioProcessingOptionsTests
{
    private const string ResultQueue = "https://sqs.us-east-1.amazonaws.com/123456789012/audio-results";
    private const string ConfirmationsQueue = "https://sqs.us-east-1.amazonaws.com/123456789012/confirmations";
    private const string ReceiptQueue = "https://sqs.us-east-1.amazonaws.com/123456789012/receipts";
    private const string LockQueue = "https://sqs.us-east-1.amazonaws.com/123456789012/lock";
    private const string Topic = "arn:aws:sns:us-east-1:123456789012:events";

    [Fact]
    public void DefaultsToDisabledWithoutAudioDependencies()
    {
        var options = Read(new Dictionary<string, string?>(), false, false);
        Assert.False(options.Enabled);
        Assert.False(options.Settings.VideoEnabled);
    }

    [Fact]
    public void AcceptsTheScopedProviderAndDedicatedResultQueue()
    {
        var options = Read(Valid());
        Assert.True(options.Enabled);
        Assert.Equal(Settings, options.Settings);
        Assert.Equal(ResultQueue, options.ResultQueueUrl);
    }

    [Fact]
    public void EnablesVideoOnlyWhenConfiguredWithVideoConfirmation()
    {
        var configuration = Valid();
        configuration["VideoEnabled"] = "true";

        var options = Read(configuration, videoConfirmationEnabled: true);

        Assert.True(options.Enabled);
        Assert.True(options.Settings.VideoEnabled);
    }

    [Theory]
    [InlineData("true", "false", true)]
    [InlineData("true", "true", false)]
    [InlineData("yes", "true", true)]
    public void RejectsVideoWithoutTheRequiredConfiguration(string flag, string enabled, bool videoConfirmationEnabled)
    {
        var configuration = Valid();
        configuration["VideoEnabled"] = flag;
        configuration["Enabled"] = enabled;

        var error = Assert.Throws<StartupConfigurationException>(() => Read(configuration, videoConfirmationEnabled: videoConfirmationEnabled));
        Assert.Equal("MediaAudioProcessing:VideoEnabled", error.OptionName);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RequiresAudioConfirmationAndMediaProcessing(bool audioEnabled, bool mediaEnabled)
    {
        var error = Assert.Throws<StartupConfigurationException>(() => Read(Valid(), audioEnabled, mediaEnabled));
        Assert.Equal("MediaAudioProcessing:Enabled", error.OptionName);
    }

    [Theory]
    [InlineData("Enabled", "yes")]
    [InlineData("Region", "us-west-2")]
    [InlineData("QueueArn", "arn:aws:mediaconvert:us-west-2:123456789012:queues/audio")]
    [InlineData("QueueArn", "arn:aws:mediaconvert:us-east-1:123456789012:queues/audio.fifo")]
    [InlineData("JobRoleArn", "arn:aws:iam::098765432109:role/audio")]
    [InlineData("JobRoleArn", "arn:aws:iam::123456789012:role/")]
    [InlineData("ResultQueueUrl", "https://sqs.us-east-1.amazonaws.com/098765432109/audio-results")]
    [InlineData("ResultQueueUrl", "https://sqs.us-east-1.amazonaws.com/123456789012/audio-results.fifo")]
    [InlineData("ResultQueueUrl", ConfirmationsQueue)]
    [InlineData("ResultQueueUrl", ReceiptQueue)]
    [InlineData("ResultQueueUrl", LockQueue)]
    [InlineData("ResultQueueUrl", "https://SQS.US-EAST-1.AMAZONAWS.COM:443/123456789012/%72eceipts/")]
    public void RejectsProviderScopeAndConsumerQueueMistakes(string option, string value)
    {
        var configuration = Valid();
        configuration[option] = value;

        var error = Assert.Throws<StartupConfigurationException>(() => Read(configuration));
        Assert.Equal("MediaAudioProcessing:" + option, error.OptionName);
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Enabled"] = "true",
        ["Region"] = Settings.Region,
        ["QueueArn"] = Settings.QueueArn,
        ["JobRoleArn"] = Settings.JobRoleArn,
        ["ResultQueueUrl"] = ResultQueue
    };

    private static MediaAudioProcessingOptions Read(Dictionary<string, string?> values, bool audioEnabled = true,
        bool mediaEnabled = true, bool videoConfirmationEnabled = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values.Select(pair =>
            KeyValuePair.Create("MediaAudioProcessing:" + pair.Key, pair.Value))).Build();
        var photo = new PhotoSubmissionsOptions(true, Bucket, ReceiptQueue, Settings.Region, null, null, null, false,
            videoConfirmationEnabled, "/usr/bin/ffprobe", AudioEnabled: audioEnabled);
        var media = new MediaProcessingOptions(mediaEnabled, Settings.Region, Topic, ConfirmationsQueue, null, null);
        var structural = new EventStructuralLockOptions(true, Settings.Region, Topic, LockQueue, null, null);
        return MediaAudioProcessingOptions.FromConfiguration(configuration, false, photo, media, structural);
    }
}
