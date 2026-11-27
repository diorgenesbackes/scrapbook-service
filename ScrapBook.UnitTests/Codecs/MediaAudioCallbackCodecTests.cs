using System.Text.Json.Nodes;
using ScrapBook.Application.Codecs;
using static ScrapBook.UnitTests.Services.MediaAudioTestFixture;

namespace ScrapBook.UnitTests.Codecs;

public sealed class MediaAudioCallbackCodecTests
{
    [Theory]
    [InlineData("COMPLETE", "complete", false)]
    [InlineData("ERROR", "error", true)]
    [InlineData("CANCELED", "canceled", false)]
    public void AcceptsOnlyTerminalStatesFromAmazonMediaConvert(string status, string state, bool resourceOmitsAccount)
    {
        var envelope = Envelope(status);
        if (resourceOmitsAccount)
            envelope["resources"] = new JsonArray($"arn:aws:mediaconvert:{Settings.Region}::jobs/{ProviderJobId}");

        Assert.Equal(Callback() with { State = state }, MediaAudioCallbackCodec.Read(envelope.ToJsonString()));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("detail-type")]
    [InlineData("version")]
    [InlineData("id")]
    [InlineData("status")]
    [InlineData("request")]
    [InlineData("account")]
    [InlineData("detail-account")]
    [InlineData("region")]
    [InlineData("queue")]
    [InlineData("job-id")]
    [InlineData("resource-job")]
    [InlineData("resource-account")]
    [InlineData("multiple-resources")]
    public void RejectsMalformedUnscopedOrNonTerminalDelivery(string invalidField)
    {
        var envelope = Envelope("COMPLETE");
        var detail = envelope["detail"]!.AsObject();
        switch (invalidField)
        {
            case "source": envelope["source"] = "other.provider"; break;
            case "detail-type": envelope["detail-type"] = "MediaConvert Job"; break;
            case "version": envelope["version"] = "1"; break;
            case "id": envelope["id"] = Guid.Empty.ToString("D"); break;
            case "status": detail["status"] = "PROGRESSING"; break;
            case "request": detail["userMetadata"]!["requestKey"] = RequestKey.ToString("D"); break;
            case "account": envelope["account"] = "123"; break;
            case "detail-account": detail["accountID"] = "098765432109"; break;
            case "region": envelope["region"] = "us-west-2"; break;
            case "queue": detail["queue"] = "arn:aws:mediaconvert:us-east-1:098765432109:queues/other"; break;
            case "job-id": detail["jobId"] = "jobs/another"; break;
            case "resource-job": envelope["resources"] = new JsonArray(JobArn() + "-another"); break;
            case "resource-account":
                envelope["resources"] = new JsonArray($"arn:aws:mediaconvert:{Settings.Region}:098765432109:jobs/{ProviderJobId}");
                break;
            default: envelope["resources"] = new JsonArray(JobArn(), JobArn()); break;
        }

        Assert.Null(MediaAudioCallbackCodec.Read(envelope.ToJsonString()));
    }

    [Fact]
    public void RejectsDuplicateClaimsAndOversizedOrMalformedMessages()
    {
        var valid = Envelope("COMPLETE").ToJsonString();
        var duplicate = valid.Insert(1, "\"source\":\"aws.mediaconvert\",");

        Assert.Null(MediaAudioCallbackCodec.Read(duplicate));
        Assert.Null(MediaAudioCallbackCodec.Read(valid + new string(' ', 65536)));
        Assert.Null(MediaAudioCallbackCodec.Read(valid[..^1]));
    }

    private static string JobArn() => $"arn:aws:mediaconvert:{Settings.Region}:{Settings.Account}:jobs/{ProviderJobId}";

    private static JsonObject Envelope(string status) => new()
    {
        ["version"] = "0",
        ["id"] = "9ddd1d0e-a613-4669-8e13-db0bc61a4494",
        ["source"] = "aws.mediaconvert",
        ["detail-type"] = "MediaConvert Job State Change",
        ["account"] = Settings.Account,
        ["region"] = Settings.Region,
        ["resources"] = new JsonArray(JobArn()),
        ["detail"] = new JsonObject
        {
            ["accountId"] = Settings.Account,
            ["jobId"] = ProviderJobId,
            ["queue"] = Settings.QueueArn,
            ["status"] = status,
            ["userMetadata"] = new JsonObject { ["requestKey"] = RequestKey.ToString("N") }
        }
    };
}
