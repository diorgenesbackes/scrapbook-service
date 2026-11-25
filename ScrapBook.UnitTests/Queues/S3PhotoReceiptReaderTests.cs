using System.Text.Json;
using ScrapBook.Infrastructure.Queues;

namespace ScrapBook.UnitTests.Queues;

public sealed class S3PhotoReceiptReaderTests
{
    [Fact]
    public void ReadsNativePutEventWithDecodedKeyAndExactProviderTime()
    {
        var receipt = Assert.Single(S3PhotoReceiptReader.Read(Event("version-1")));

        Assert.Equal("lab-photos", receipt.Bucket);
        Assert.Equal("submissions/temporary/a b+c", receipt.Key);
        Assert.Equal("version-1", receipt.VersionId);
        Assert.Equal(20_000_000, receipt.Size);
        Assert.Equal(new DateTimeOffset(2032, 3, 1, 16, 0, 0, TimeSpan.Zero).AddTicks(1), receipt.ReceivedAt);
        Assert.Equal(TimeSpan.Zero, receipt.ReceivedAt.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public void RejectsUnversionedPutEvent(string version) =>
        Assert.Throws<JsonException>(() => S3PhotoReceiptReader.Read(Event(version)));

    [Fact]
    public void RejectsPutEventWithoutVersionField() =>
        Assert.Throws<JsonException>(() => S3PhotoReceiptReader.Read(Event(null)));

    [Fact]
    public void RecognizesS3TestEventWithoutProducingAReceipt() =>
        Assert.Empty(S3PhotoReceiptReader.Read("""{"Event":"s3:TestEvent"}"""));

    private static string Event(string? version)
    {
        var storageObject = new Dictionary<string, object>
        {
            ["key"] = "submissions%2Ftemporary%2Fa+b%2Bc",
            ["size"] = 20_000_000
        };
        if (version is not null) storageObject["versionId"] = version;

        return JsonSerializer.Serialize(new
        {
            Records = new[]
            {
                new
                {
                    eventVersion = "2.1",
                    eventSource = "aws:s3",
                    eventName = "ObjectCreated:Put",
                    eventTime = "2032-03-01T16:00:00.0000001Z",
                    s3 = new { bucket = new { name = "lab-photos" }, @object = storageObject }
                }
            }
        });
    }
}
