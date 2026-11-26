using Microsoft.Extensions.Configuration;
using ScrapBook.Api.Options;

namespace ScrapBook.UnitTests.Options;

public sealed class EventStructuralLockOptionsTests
{
    [Theory]
    [InlineData("https://sqs.us-east-1.amazonaws.com/123456789012/structural-lock", false, true)]
    [InlineData("https://sqs.us-east-1.amazonaws.com/structural-lock", false, false)]
    [InlineData("https://sqs.us-east-1.amazonaws.com/987654321012/structural-lock", false, false)]
    [InlineData("https://sqs.us-west-2.amazonaws.com/123456789012/structural-lock", false, false)]
    [InlineData("https://sqs.us-east-1.amazonaws.com:8443/123456789012/structural-lock", false, false)]
    [InlineData("https://sqs.us-east-1.amazonaws.com.example.test/123456789012/structural-lock", false, false)]
    [InlineData("http://queue-emulator:5000/123456789012/structural-lock", true, true)]
    public void ValidatesTheStandardQueueUrlForTheConfiguredEnvironment(string queueUrl, bool development, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EventStructuralLock:Enabled"] = "true",
            ["EventStructuralLock:Region"] = "us-east-1",
            ["EventStructuralLock:TopicArn"] = "arn:aws:sns:us-east-1:123456789012:events",
            ["EventStructuralLock:QueueUrl"] = queueUrl
        }).Build();

        if (valid)
        {
            var options = EventStructuralLockOptions.FromConfiguration(configuration, development);
            Assert.True(options.Enabled);
            Assert.Equal(queueUrl, options.QueueUrl);
        }
        else
        {
            var error = Assert.Throws<StartupConfigurationException>(
                () => EventStructuralLockOptions.FromConfiguration(configuration, development));
            Assert.Equal("EventStructuralLock:QueueUrl", error.OptionName);
        }
    }
}
