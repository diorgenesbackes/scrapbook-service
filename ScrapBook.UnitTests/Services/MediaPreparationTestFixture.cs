using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal static class MediaPreparationTestFixture
{
    public static MediaConfirmationModel Confirmation { get; } = new(
        Guid.Parse("15852452-1487-6464-d920-f0180572f95f"), 200, 560, 701,
        new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero),
        Guid.Parse("159c9246-d1d4-4841-bc26-cee6c7c6a6d1"));

    public static byte[] Original { get; } = [11, 22, 33, 44];

    public static MediaSourceEntity Source()
    {
        var confirmation = Confirmation;
        var civilConfirmation = new DateTime(2026, 11, 27, 11, 0, 0);
        var sha256 = SHA256.HashData(Original);
        return new(confirmation.EventId, confirmation.RecordId, confirmation.FileId, "photo", "confirmed", "valid", "pending", 1,
            civilConfirmation, civilConfirmation, "private-media", "submissions/original/pinned", "confirmed-version",
            Original.Length, sha256, [.. sha256]);
    }

    public static OutboxMessageEntity Outbox()
    {
        var confirmation = Confirmation;
        var payload = JsonSerializer.Serialize(new { eventId = "200", recordId = "560", fileId = "701", confirmedAt = confirmation.ConfirmedAt });
        return new(confirmation.MessageId, "record.media-confirmed.v1", 1, "event", confirmation.EventId, payload,
            confirmation.CorrelationId, confirmation.OccurredAt, 1, null, "published");
    }
}
