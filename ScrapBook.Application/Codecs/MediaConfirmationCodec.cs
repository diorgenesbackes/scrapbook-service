using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Codecs;

public static class MediaConfirmationCodec
{
    private const int MaximumBytes = 16 * 1024;
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowDuplicateProperties = false, MaxDepth = 16 };

    public static MediaConfirmationModel? ReadEnvelope(string body)
    {
        if (!Fits(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body, ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadGuid(root, "messageId", out var messageId)
                || !TryReadText(root, "type", out var type) || type != MediaConfirmationModel.Type
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt16(out var number) || number != MediaConfirmationModel.Version
                || !TryReadId(root, "eventId", out var eventId)
                || !TryReadInstant(root, "occurredAt", out var occurredAt)
                || !TryReadGuid(root, "correlationId", out var correlationId)
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !TryReadId(data, "recordId", out var recordId)
                || !TryReadId(data, "fileId", out var fileId)
                || !TryReadInstant(data, "confirmedAt", out var confirmedAt) || occurredAt != confirmedAt
                || messageId != MessageId(fileId))
            {
                return null;
            }
            return new(messageId, eventId, recordId, fileId, occurredAt, confirmedAt, correlationId);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static MediaConfirmationModel? ReadOutbox(OutboxMessageEntity row)
    {
        if (row.AggregateType != "event" || row.Type != MediaConfirmationModel.Type
            || row.ProtocolVersion != MediaConfirmationModel.Version || !Fits(row.Payload))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(row.Payload, ReadOptions);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object || !TryReadId(payload, "eventId", out var eventId)
                || eventId != row.EventId || !TryReadId(payload, "recordId", out var recordId)
                || !TryReadId(payload, "fileId", out var fileId)
                || !TryReadInstant(payload, "confirmedAt", out var confirmedAt)
                || MicrosecondPrecision(confirmedAt) != MicrosecondPrecision(row.OccurredAt)
                || row.MessageId != MessageId(fileId) || row.CorrelationId == Guid.Empty)
            {
                return null;
            }
            return new(row.MessageId, eventId, recordId, fileId, confirmedAt, confirmedAt, row.CorrelationId);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string WriteEnvelope(MediaConfirmationModel message) => JsonSerializer.Serialize(new
    {
        messageId = message.MessageId,
        type = MediaConfirmationModel.Type,
        version = MediaConfirmationModel.Version,
        eventId = message.EventId.ToString(CultureInfo.InvariantCulture),
        occurredAt = message.OccurredAt,
        correlationId = message.CorrelationId,
        data = new
        {
            recordId = message.RecordId.ToString(CultureInfo.InvariantCulture),
            fileId = message.FileId.ToString(CultureInfo.InvariantCulture),
            confirmedAt = message.ConfirmedAt
        }
    });

    public static Guid MessageId(long fileId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            MediaConfirmationModel.Type + ":" + fileId.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));

    public static byte[] Hash(MediaConfirmationModel message) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(WriteEnvelope(message)));

    internal static DateTimeOffset MicrosecondPrecision(DateTimeOffset instant) => instant.AddTicks(-(instant.UtcTicks % 10));

    private static bool Fits(string body) => body.Length <= MaximumBytes && Encoding.UTF8.GetByteCount(body) <= MaximumBytes;

    private static bool TryReadId(JsonElement root, string property, out long value)
    {
        value = 0;
        return TryReadText(root, property, out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static bool TryReadGuid(JsonElement root, string property, out Guid value)
    {
        value = default;
        return TryReadText(root, property, out var text) && Guid.TryParseExact(text, "D", out value) && value != Guid.Empty;
    }

    private static bool TryReadInstant(JsonElement root, string property, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            && element.GetString() is { } text && (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
            && element.TryGetDateTimeOffset(out value);
    }

    private static bool TryReadText(JsonElement root, string property, out string? value)
    {
        value = null;
        return root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            && (value = element.GetString()) is not null;
    }
}
