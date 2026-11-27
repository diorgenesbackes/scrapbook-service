using System.Security.Cryptography;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

internal static class MediaConfirmationService
{
    internal static readonly PhotoPreparationProfileModel PhotoProfile = new(2048, 400, 85);
    internal static string[] PhotoKinds => ["image", "thumbnail"];

    internal static bool MatchesSource(MediaConfirmationModel message, MediaSourceEntity? original)
    {
        var confirmedAt = SubmissionService.Civil(MediaConfirmationCodec.MicrosecondPrecision(message.ConfirmedAt));
        return original is
        {
            RecordState: "confirmed", ValidationState: "valid", Size: > 0, Sha256.Length: 32, OriginalSha256.Length: 32
        } && original.EventId == message.EventId && original.RecordId == message.RecordId && original.FileId == message.FileId
            && original.RecordConfirmedAt == confirmedAt && original.FileConfirmedAt == confirmedAt
            && original.ProfileVersion == 1 && original.Kind is "photo" or "audio" or "video"
            && IsVersioned(original.VersionId)
            && original.ObjectKey.StartsWith("submissions/original/", StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(original.Sha256, original.OriginalSha256);
    }

    internal static bool IsVersioned(string versionId) => !string.IsNullOrWhiteSpace(versionId) && versionId != "null";

    internal static MediaConfirmationModel Confirmation(MediaJobEntity job) => new(job.ConfirmationMessageId, job.EventId,
        job.RecordId, job.FileId, job.ConfirmedAt, job.ConfirmedAt, job.ConfirmationCorrelationId);
}
