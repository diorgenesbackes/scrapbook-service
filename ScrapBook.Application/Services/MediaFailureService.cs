using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

internal static class MediaFailureService
{
    private const string Type = "record.media-preparation.failed.v1";

    internal static MediaFailureEntity Failed(MediaConfirmationModel original, int profileVersion, int? retryCycle,
        string stage, string code, DateTimeOffset occurredAt)
    {
        var identity = retryCycle?.ToString(CultureInfo.InvariantCulture) ?? stage;
        var messageId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{Type}:{original.MessageId:D}:{profileVersion}:{identity}"))).AsSpan(0, 16));
        var payload = JsonSerializer.Serialize(new
        {
            recordId = original.RecordId.ToString(CultureInfo.InvariantCulture),
            fileId = original.FileId.ToString(CultureInfo.InvariantCulture),
            profileVersion,
            stage,
            code
        });
        return new MediaFailureEntity(messageId, Type, original.EventId, payload, original.CorrelationId,
            original.MessageId, occurredAt);
    }
}
