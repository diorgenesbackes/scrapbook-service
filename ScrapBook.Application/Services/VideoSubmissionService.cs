using System.Text.Json;
using System.Security.Cryptography;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class VideoSubmissionService(IPhotoSubmissionRepository submissions, IVideoSubmissionRepository videos,
    IVideoSubmissionStorage storage, IVideoSubmissionDecoder decoder, TimeProvider clock)
{
    private const int MaximumSize = 500_000_000;
    private const int PartSize = 16 * 1024 * 1024;
    private const int MultipartThreshold = 2 * PartSize;
    private static readonly TimeSpan InitializationLease = TimeSpan.FromSeconds(30);

    internal static PhotoManifestModel NormalizeFile(PhotoSubmissionFileRequest file)
    {
        if (file.DeclaredMediaType is not ("video/mp4" or "video/quicktime" or "video/webm"))
        {
            throw new EventRequestValidationException("files[0].declaredMediaType");
        }
        if (file.SizeBytes is < 1 or > MaximumSize)
        {
            throw new EventRequestValidationException("files[0].sizeBytes");
        }

        var sha256 = PhotoSubmissionService.ReadChecksum(file.Sha256, "files[0].sha256");
        if (file.SizeBytes < MultipartThreshold)
        {
            if (file.PartSha256.ValueKind != JsonValueKind.Undefined) throw new EventRequestValidationException("files[0].partSha256");
            return new PhotoManifestModel(file.FileKey, file.DeclaredMediaType, file.SizeBytes, sha256);
        }

        var count = (file.SizeBytes + PartSize - 1) / PartSize;
        var parts = file.PartSha256;
        if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != count)
        {
            throw new EventRequestValidationException("files[0].partSha256");
        }

        var checksums = parts.EnumerateArray().Select((part, index) => PhotoSubmissionService.ReadChecksum(
            part.ValueKind == JsonValueKind.String ? part.GetString() : null, $"files[0].partSha256[{index}]")).ToArray();
        return new PhotoManifestModel(file.FileKey, file.DeclaredMediaType, file.SizeBytes, sha256, checksums);
    }

    internal async Task<MediaUploadStatusResponse> GetUploadStatusAsync(PhotoSubmissionEntity file,
        CancellationToken cancellationToken)
    {
        var deadline = PhotoSubmissionService.Deadline(file.CompletionDeadlineAt);
        if (file.PartSha256 is not { } checksums)
        {
            return new MediaUploadStatusResponse("put", null, file.TemporaryVersion is null ? [] : [1], deadline);
        }

        if (file.TemporaryVersion is not null)
        {
            return new MediaUploadStatusResponse("multipart", PartSize, Enumerable.Range(1, checksums.Length).ToArray(), deadline);
        }

        var uploaded = file.State == "receiving" && file.MultipartId is { } uploadId
            ? await ReadUploadedPartsAsync(file, uploadId, checksums, cancellationToken) : [];
        return new MediaUploadStatusResponse("multipart", PartSize, uploaded, deadline);
    }

    private async Task<int[]> ReadUploadedPartsAsync(PhotoSubmissionEntity file, string uploadId, byte[][] checksums,
        CancellationToken cancellationToken)
    {
        var parts = await storage.ListPartsAsync(file.Bucket, file.TemporaryKey, uploadId, cancellationToken);
        return parts is null ? [] : MatchingParts(file, checksums, parts).Select(part => part.PartNumber).ToArray();
    }

    internal async Task<PhotoUploadAuthorizationResponse> AuthorizePartAsync(PhotoSubmissionEntity file, int? partNumber,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var checksums = file.PartSha256 ?? throw new InvalidOperationException("Multipart manifest is missing.");
        if (partNumber is not { } number || number < 1 || number > checksums.Length)
        {
            throw new EventRequestValidationException("partNumber");
        }

        var uploadId = await EnsureMultipartAsync(file, cancellationToken);
        var parts = await storage.ListPartsAsync(file.Bucket, file.TemporaryKey, uploadId, cancellationToken);
        if (parts is null || MatchingParts(file, checksums, parts).Any(part => part.PartNumber == number))
        {
            throw new VideoUploadUnavailableException();
        }
        if (clock.GetUtcNow() >= expiresAt) throw new SubmissionDeadlineExceededException();
        return await storage.AuthorizePartAsync(file.Bucket, file.TemporaryKey, uploadId, number, PartLength(file, number),
            checksums[number - 1], expiresAt, cancellationToken);
    }

    private async Task<string> EnsureMultipartAsync(PhotoSubmissionEntity file, CancellationToken cancellationToken)
    {
        if (file.MultipartId is { } current) return current;

        var now = clock.GetUtcNow();
        var claim = await videos.TryBeginMultipartAsync(file.TemporaryObjectId, file.SessionId, now, now - InitializationLease,
            cancellationToken);
        if (claim is { } version)
        {
            var existing = await storage.ListMultipartUploadsAsync(file.Bucket, file.TemporaryKey, cancellationToken);
            if (clock.GetUtcNow() >= PhotoSubmissionService.Deadline(file.CompletionDeadlineAt))
            {
                throw new SubmissionDeadlineExceededException();
            }
            var selected = existing.Order(StringComparer.Ordinal).FirstOrDefault()
                ?? await storage.CreateMultipartUploadAsync(file.Bucket, file.TemporaryKey, file.DeclaredMediaType, cancellationToken);
            if (await videos.TrySetMultipartIdAsync(file.TemporaryObjectId, file.SessionId, version, selected, cancellationToken))
            {
                return selected;
            }
        }

        var updated = await submissions.FindForRecordAsync(file.RecordId, file.FileKey, cancellationToken);
        if (updated?.State != "receiving" || updated.TemporaryVersion is not null) throw new VideoUploadUnavailableException();
        return updated.MultipartId ?? throw new PhotoStorageUnavailableException();
    }

    internal async Task CompleteAsync(PhotoSubmissionEntity file, CancellationToken cancellationToken)
    {
        var deadline = PhotoSubmissionService.Deadline(file.CompletionDeadlineAt);
        if (file.State != "receiving" || file.TemporaryVersion is not null || clock.GetUtcNow() >= deadline
            || file.MultipartId is not { } uploadId || file.PartSha256 is not { } checksums)
        {
            return;
        }

        var parts = await storage.ListPartsAsync(file.Bucket, file.TemporaryKey, uploadId, cancellationToken);
        if (parts is null || parts.Count != checksums.Length) return;

        var matching = MatchingParts(file, checksums, parts);
        if (matching.Length == checksums.Length && clock.GetUtcNow() < deadline)
        {
            await storage.CompleteMultipartUploadAsync(file.Bucket, file.TemporaryKey, uploadId, matching, cancellationToken);
        }
    }

    internal async Task<bool> AbortOpenUploadsAsync(PhotoSubmissionEntity file, CancellationToken cancellationToken)
    {
        if (file.PartSha256 is null) return true;

        var open = await storage.ListMultipartUploadsAsync(file.Bucket, file.TemporaryKey, cancellationToken);
        if (open.Count == 0) return true;
        foreach (var uploadId in open)
        {
            await storage.AbortMultipartUploadAsync(file.Bucket, file.TemporaryKey, uploadId, cancellationToken);
        }
        var remaining = await storage.ListMultipartUploadsAsync(file.Bucket, file.TemporaryKey, cancellationToken);
        return remaining.Count == 0;
    }

    internal async Task<bool> CleanMultipartAsync(DateTimeOffset now, TimeSpan cleanupDelay, CancellationToken cancellationToken)
    {
        var file = await videos.FindMultipartCleanupAsync(now - cleanupDelay, cancellationToken);
        if (file is null || !await AbortOpenUploadsAsync(file, cancellationToken)) return false;

        var deadline = PhotoSubmissionService.Deadline(file.CompletionDeadlineAt);
        var anchor = file.State != "expired" && file.TerminalAt > deadline ? file.TerminalAt.Value : deadline;
        await videos.MarkMultipartAbortedAsync(file.TemporaryObjectId, anchor + cleanupDelay, clock.GetUtcNow(),
            cancellationToken);
        return true;
    }

    internal async Task<bool> IsLateReceiptAsync(PhotoSubmissionEntity file, string versionId, CancellationToken cancellationToken)
    {
        var kind = file.PartSha256 is null ? "put" : "complete_multipart";
        var receivedAt = await videos.FindReceiptAtAsync(file.TemporaryObjectId, versionId, kind, cancellationToken);
        return receivedAt >= PhotoSubmissionService.Deadline(file.CompletionDeadlineAt);
    }

    internal async Task<VideoMetadataModel> ValidateAsync(PhotoSubmissionEntity file, string version,
        CancellationToken cancellationToken)
    {
        var video = await decoder.DecodeAsync(file.Bucket, file.TemporaryKey, version, file.DeclaredSize, cancellationToken);
        var maximumDuration = file.Category == "moment" ? 60 : 300;
        if (video.MediaType != file.DeclaredMediaType || video.Duration > maximumDuration
            || decimal.Round(video.Duration, 6, MidpointRounding.AwayFromZero) <= 0
            || video.Width <= 0 || video.Height <= 0 || video.FrameRateNumerator <= 0 || video.FrameRateDenominator <= 0
            || !CryptographicOperations.FixedTimeEquals(video.Sha256, file.Sha256))
        {
            throw new PhotoContentInvalidException();
        }
        return video;
    }

    internal async Task VerifyOriginalAsync(PhotoSubmissionEntity file, string version, CancellationToken cancellationToken)
    {
        var checksum = await storage.ReadSha256Async(file.Bucket, file.OriginalKey, version, file.DeclaredSize, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(checksum, file.Sha256)) throw new PhotoStorageUnavailableException();
    }

    private static int PartLength(PhotoSubmissionEntity file, int number) =>
        Math.Min(PartSize, file.DeclaredSize - (number - 1) * PartSize);

    private static VideoPartModel[] MatchingParts(PhotoSubmissionEntity file, byte[][] checksums, IReadOnlyList<VideoPartModel> parts) =>
        parts.GroupBy(part => part.PartNumber).Where(group => group.Count() == 1).Select(group => group.Single())
            .Where(part => part.PartNumber >= 1 && part.PartNumber <= checksums.Length
                && part.Size == PartLength(file, part.PartNumber) && !string.IsNullOrWhiteSpace(part.ETag)
                && CryptographicOperations.FixedTimeEquals(part.Sha256, checksums[part.PartNumber - 1]))
            .OrderBy(part => part.PartNumber).ToArray();
}
