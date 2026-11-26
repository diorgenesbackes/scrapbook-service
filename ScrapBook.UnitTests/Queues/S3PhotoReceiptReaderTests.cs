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
        Assert.Equal("put", receipt.EventKind);
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

    [Fact]
    public void ReadsMultipartCompletionWithProviderVersionAndInstant()
    {
        var receipt = Assert.Single(S3PhotoReceiptReader.Read(Event("uploaded-video", "ObjectCreated:CompleteMultipartUpload")));

        Assert.Equal("complete_multipart", receipt.EventKind);
        Assert.Equal("uploaded-video", receipt.VersionId);
        Assert.Equal(new DateTimeOffset(2032, 3, 1, 16, 0, 0, TimeSpan.Zero).AddTicks(1), receipt.ReceivedAt);
    }

    [Theory]
    [InlineData("ObjectCreated:Copy")]
    [InlineData("ObjectRemoved:Delete")]
    [InlineData("UploadPart")]
    public void RejectsEventOutsideReceiptKinds(string kind) =>
        Assert.Throws<JsonException>(() => S3PhotoReceiptReader.Read(Event("version-1", kind)));

    private static string Event(string? version, string eventName = "ObjectCreated:Put")
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
                    eventName,
                    eventTime = "2032-03-01T16:00:00.0000001Z",
                    s3 = new { bucket = new { name = "lab-photos" }, @object = storageObject }
                }
            }
        });
    }
}
