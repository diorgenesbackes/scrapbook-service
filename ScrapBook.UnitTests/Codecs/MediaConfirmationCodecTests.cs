using System.Text.Json;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Codecs;

public sealed class MediaConfirmationCodecTests
{
    private const string ValidEnvelope = """
        {
          "messageId": "15852452-1487-6464-d920-f0180572f95f",
          "type": "record.media-confirmed.v1",
          "version": 1,
          "occurredAt": "2026-11-27T14:00:00.123456Z",
          "eventId": "200",
          "correlationId": "159c9246-d1d4-4841-bc26-cee6c7c6a6d1",
          "data": {
            "recordId": "560",
            "fileId": "701",
            "confirmedAt": "2026-11-27T14:00:00.123456Z"
          }
        }
        """;

    private static MediaConfirmationModel Confirmation { get; } = new(
        Guid.Parse("15852452-1487-6464-d920-f0180572f95f"), 200, 560, 701,
        new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero).AddTicks(1_234_560),
        new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero).AddTicks(1_234_560),
        Guid.Parse("159c9246-d1d4-4841-bc26-cee6c7c6a6d1"));

    [Fact]
    public void ReadsTheF08ContractWithItsExactFileAndCorrelation()
    {
        Assert.Equal(Confirmation, MediaConfirmationCodec.ReadEnvelope(ValidEnvelope));
    }

    [Fact]
    public void WritesBusinessIdsAsDecimalStrings()
    {
        var envelope = MediaConfirmationCodec.WriteEnvelope(Confirmation);
        using var document = JsonDocument.Parse(envelope);
        var root = document.RootElement;
        var data = root.GetProperty("data");

        Assert.Equal("record.media-confirmed.v1", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("200", root.GetProperty("eventId").GetString());
        Assert.Equal("560", data.GetProperty("recordId").GetString());
        Assert.Equal("701", data.GetProperty("fileId").GetString());
        Assert.Equal(Confirmation, MediaConfirmationCodec.ReadEnvelope(envelope));
    }

    [Theory]
    [InlineData("record.media-confirmed.v1", "event.first-record-confirmed.v1")]
    [InlineData("\"version\": 1", "\"version\": 2")]
    [InlineData("\"version\": 1", "\"version\": \"1\"")]
    [InlineData("\"eventId\": \"200\"", "\"eventId\": 200")]
    [InlineData("\"560\"", "\"-560\"")]
    [InlineData("\"701\"", "\"0\"")]
    [InlineData("\"701\"", "\"9223372036854775808\"")]
    [InlineData("\"701\"", "\"702\"")]
    [InlineData("\"fileId\": \"701\",", "")]
    [InlineData("\"fileId\": \"701\",", "\"fileId\": \"701\", \"fileId\": \"702\",")]
    [InlineData("2026-11-27T14:00:00.123456Z", "2026-11-27T11:00:00.123456-03:00")]
    [InlineData("159c9246-d1d4-4841-bc26-cee6c7c6a6d1", "00000000-0000-0000-0000-000000000000")]
    public void RejectsMessagesOutsideTheConfirmationContract(string original, string replacement)
    {
        Assert.Null(MediaConfirmationCodec.ReadEnvelope(ValidEnvelope.Replace(original, replacement, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public void RejectsMalformedEnvelopes(string body)
    {
        Assert.Null(MediaConfirmationCodec.ReadEnvelope(body));
    }
}
