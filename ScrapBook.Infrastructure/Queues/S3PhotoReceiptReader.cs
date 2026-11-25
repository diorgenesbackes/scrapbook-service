using System.Globalization;
using System.Net;
using System.Text.Json;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Queues;

public static class S3PhotoReceiptReader
{
    public static IReadOnlyList<PhotoReceiptModel> Read(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (json.RootElement.TryGetProperty("Event", out var testEvent) && testEvent.GetString() == "s3:TestEvent") return [];
            var records = json.RootElement.GetProperty("Records");
            return records.EnumerateArray().Select(ReadRecord).ToArray();
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new JsonException("Invalid photo receipt event.");
        }
    }

    private static PhotoReceiptModel ReadRecord(JsonElement record)
    {
        if (record.GetProperty("eventSource").GetString() != "aws:s3"
            || record.GetProperty("eventName").GetString() != "ObjectCreated:Put"
            || record.GetProperty("eventVersion").GetString() is not { } eventVersion
            || !eventVersion.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new JsonException();
        }

        var s3 = record.GetProperty("s3");
        var bucket = s3.GetProperty("bucket").GetProperty("name").GetString();
        var storageObject = s3.GetProperty("object");
        var key = storageObject.GetProperty("key").GetString();
        var versionId = storageObject.GetProperty("versionId").GetString();
        var size = storageObject.GetProperty("size").GetInt64();
        var eventTime = record.GetProperty("eventTime").GetString();
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(key)
            || versionId is not { Length: > 0 and <= 1024 } or "null" || size < 0
            || eventTime is null || !DateTimeOffset.TryParseExact(eventTime, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
                "yyyy-MM-dd'T'HH:mm:ssK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var receivedAt)
            || !eventTime.EndsWith('Z'))
        {
            throw new JsonException();
        }
        return new PhotoReceiptModel(bucket, WebUtility.UrlDecode(key), versionId, size, receivedAt.ToUniversalTime());
    }
}
