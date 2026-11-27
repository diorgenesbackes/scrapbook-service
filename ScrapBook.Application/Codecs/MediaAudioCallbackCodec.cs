using System.Text;
using System.Text.Json;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Codecs;

public static class MediaAudioCallbackCodec
{
    private const int MaximumBytes = 64 * 1024;
    private static readonly JsonDocumentOptions Options = new() { AllowDuplicateProperties = false, MaxDepth = 16 };

    public static MediaAudioCallbackModel? Read(string body)
    {
        if (body.Length > MaximumBytes || Encoding.UTF8.GetByteCount(body) > MaximumBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(body, Options);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MediaAudioCallbackModel? Read(JsonElement root)
    {
        if (Text(root, "version") != "0" || Text(root, "source") != "aws.mediaconvert"
            || Text(root, "detail-type") != "MediaConvert Job State Change"
            || !Guid.TryParseExact(Text(root, "id"), "D", out var eventId) || eventId == Guid.Empty
            || Text(root, "account") is not { Length: 12 } account || !account.All(char.IsAsciiDigit)
            || Text(root, "region") is not { Length: > 0 and <= 32 } region || !region.All(RegionCharacter)
            || !root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object
            || !MatchesDetailAccount(detail, account)
            || Text(detail, "jobId") is not { Length: > 0 and <= 128 } jobId || !jobId.All(JobIdCharacter))
        {
            return null;
        }
        var state = Text(detail, "status") switch
        {
            "COMPLETE" => "complete",
            "ERROR" => "error",
            "CANCELED" => "canceled",
            _ => null
        };
        if (state is null || Text(detail, "queue") is not { } queue || !MatchesResources(root, queue, account, region, jobId)
            || !detail.TryGetProperty("userMetadata", out var metadata)
            || !Guid.TryParseExact(Text(metadata, "requestKey"), "N", out var requestKey) || requestKey == Guid.Empty)
        {
            return null;
        }
        return new(account, region, queue, jobId, requestKey, state);
    }

    private static bool MatchesResources(JsonElement root, string queue, string account, string region, string jobId)
    {
        if (queue.Split(':') is not ["arn", ("aws" or "aws-cn" or "aws-us-gov") and var partition, "mediaconvert",
            var queueRegion, var queueAccount, var name] || queueRegion != region || queueAccount != account
            || !name.StartsWith("queues/", StringComparison.Ordinal) || name.Length == "queues/".Length
            || !root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array
            || resources.GetArrayLength() != 1 || resources[0].ValueKind != JsonValueKind.String)
        {
            return false;
        }
        var arn = resources[0].GetString();
        var prefix = $"arn:{partition}:mediaconvert:{region}:";
        return arn == $"{prefix}{account}:jobs/{jobId}" || arn == $"{prefix}:jobs/{jobId}";
    }

    private static bool MatchesDetailAccount(JsonElement detail, string account) =>
        (!detail.TryGetProperty("accountId", out _) || Text(detail, "accountId") == account)
        && (!detail.TryGetProperty("accountID", out _) || Text(detail, "accountID") == account);

    private static bool RegionCharacter(char character) => character is >= 'a' and <= 'z' or '-' || char.IsAsciiDigit(character);

    private static bool JobIdCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
