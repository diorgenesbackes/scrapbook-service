using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class PhotoSubmissionService(IPhotoSubmissionRepository photos, ISubmissionRepository submissions,
    IPhotoSubmissionStorage storage, IPhotoSubmissionDecoder decoder, PhotoSubmissionSettings settings, TimeProvider clock,
    IPhotoSubmissionReporter? reporter = null, VideoSubmissionService? video = null)
{
    public const string TemporaryPrefix = "submissions/temporary/";
    private const string OriginalPrefix = "submissions/original/";
    private const string MediaConfirmation = "record.media-confirmed.v1";
    private const int MaximumSize = 20_000_000;
    private static readonly TimeSpan CleanupDelay = TimeSpan.FromHours(24);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(10);

    internal static (string? Text, string? Caption, IReadOnlyList<PhotoManifestModel> Files, string CanonicalPayload) NormalizePayload(
        CreateSubmissionRequest request)
    {
        if (request.Text is not null) throw new EventRequestValidationException("text");
        var isVideo = request.Format == "video";
        var maximumFiles = request.Category == "moment" && !isVideo ? 10 : 1;
        if (request.Files is not { Count: > 0 } files || files.Count > maximumFiles)
        {
            throw new EventRequestValidationException("files");
        }

        var keys = new HashSet<Guid>();
        var manifests = new List<PhotoManifestModel>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            if (files[index] is not { } file || file.FileKey == Guid.Empty || file.Position != index + 1 || !keys.Add(file.FileKey))
            {
                throw new EventRequestValidationException("files");
            }
            manifests.Add(isVideo ? VideoSubmissionService.NormalizeFile(file) : NormalizeFile(file, index));
        }

        var caption = SubmissionService.NormalizeLineEndings(request.Caption);
        if (caption is not null) SubmissionService.EnsureText(caption, 500, "caption", requireContent: false);
        var canonicalPayload = request.Category == "testimonial"
            ? JsonSerializer.Serialize(new { request.Category, request.Format, caption, manifest = manifests[0] })
            : JsonSerializer.Serialize(new { request.Category, request.Format, caption, manifests });
        return (null, caption, manifests, "\0" + canonicalPayload);
    }

    private static PhotoManifestModel NormalizeFile(PhotoSubmissionFileRequest file, int index)
    {
        if (file.DeclaredMediaType is not ("image/jpeg" or "image/png" or "image/heic" or "image/heif"))
        {
            throw new EventRequestValidationException($"files[{index}].declaredMediaType");
        }
        if (file.SizeBytes is < 1 or > MaximumSize)
        {
            throw new EventRequestValidationException($"files[{index}].sizeBytes");
        }
        if (file.PartSha256.ValueKind != JsonValueKind.Undefined) throw new EventRequestValidationException($"files[{index}].partSha256");
        var hash = ReadChecksum(file.Sha256, $"files[{index}].sha256");
        return new PhotoManifestModel(file.FileKey, file.DeclaredMediaType, file.SizeBytes, hash);
    }

    internal static byte[] ReadChecksum(string? value, string field)
    {
        var hash = new byte[32];
        if (value is not { Length: 44 } || !Convert.TryFromBase64String(value, hash, out var written)
            || written != hash.Length || Convert.ToBase64String(hash) != value)
        {
            throw new EventRequestValidationException(field);
        }
        return hash;
    }

    internal async Task<SubmissionStatusResponse> AdmitAsync(EventEntity item, long noticeId, string category, string format, string name,
        string? caption, IReadOnlyList<PhotoManifestModel> files, DateTimeOffset consentedAt, DateTimeOffset ingress,
        string operation, Guid attemptKey, string keyId, byte[] payloadHash, byte[] authorizationHash, Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (format == "video") RequireVideoEnabled();
        var intents = files.Select(CreateIntent).ToArray();
        var command = new CreatePhotoSubmissionCommand(item, noticeId, category, name, caption, intents,
            SubmissionService.Civil(consentedAt), SubmissionService.Civil(ingress), operation, attemptKey, keyId, payloadHash,
            authorizationHash, ingress.AddDays(30), correlationId, settings.Bucket, format);
        await photos.InsertAsync(command, cancellationToken);
        var pending = category == "moment" && format == "photo" ? intents.Select(file => file.Position).ToArray() : null;
        return new SubmissionStatusResponse("receiving", CompletionDeadlineAt: Deadline(item.CompletionDeadlineAt),
            PendingPositions: pending);
    }

    private static PhotoUploadIntentModel CreateIntent(PhotoManifestModel file, int index)
    {
        var objectKey = Guid.NewGuid().ToString("N");
        return new PhotoUploadIntentModel(file, index + 1, TemporaryPrefix + objectKey, OriginalPrefix + objectKey);
    }

    public Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(long recordId, Guid fileKey, DateTimeOffset credentialExpiry,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(recordId, fileKey, credentialExpiry, null, cancellationToken);

    public async Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(long recordId, Guid fileKey, DateTimeOffset credentialExpiry,
        int? partNumber, CancellationToken cancellationToken)
    {
        var submission = await FindFileAsync(recordId, fileKey, cancellationToken);
        var now = clock.GetUtcNow();
        var expiresAt = Deadline(submission.CompletionDeadlineAt);
        if (expiresAt <= now) throw new SubmissionDeadlineExceededException();
        if (submission.State != "receiving" || submission.TemporaryVersion is not null)
        {
            if (submission.Format == "video") throw new VideoUploadUnavailableException();
            throw new PhotoUploadUnavailableException();
        }
        if (submission.Format == "video") RequireVideoEnabled();
        if (credentialExpiry < expiresAt) expiresAt = credentialExpiry;
        var shortExpiry = now.Add(AuthorizationLifetime);
        if (shortExpiry < expiresAt) expiresAt = shortExpiry;
        if (expiresAt <= now) throw new SubmissionAttemptNotFoundException();
        if (submission.PartSha256 is not null)
        {
            return await RequireVideoService().AuthorizePartAsync(submission, partNumber, expiresAt, cancellationToken);
        }
        if (partNumber is not null) throw new EventRequestValidationException("partNumber");
        return await storage.AuthorizeAsync(submission.Bucket, submission.TemporaryKey, submission.DeclaredMediaType,
            submission.DeclaredSize, submission.Sha256, expiresAt, cancellationToken);
    }

    internal async Task<MediaUploadStatusResponse> GetUploadStatusAsync(long recordId, Guid fileKey, CancellationToken cancellationToken)
    {
        var submission = await FindFileAsync(recordId, fileKey, cancellationToken);
        if (submission.Format == "video")
        {
            return await RequireVideoService().GetUploadStatusAsync(submission, cancellationToken);
        }
        return new MediaUploadStatusResponse("put", null, submission.TemporaryVersion is null ? [] : [1],
            Deadline(submission.CompletionDeadlineAt));
    }

    internal async Task SignalVideoCompletionAsync(long recordId, CancellationToken cancellationToken)
    {
        var files = await photos.FindFilesAsync(recordId, cancellationToken);
        var file = files.Single();
        await RequireVideoService().CompleteAsync(file, cancellationToken);
    }

    private async Task<PhotoSubmissionEntity> FindFileAsync(long recordId, Guid fileKey, CancellationToken cancellationToken) =>
        await photos.FindForRecordAsync(recordId, fileKey, cancellationToken) ?? throw new SubmissionAttemptNotFoundException();

    private void RequireVideoEnabled()
    {
        if (!settings.VideoEnabled) throw new VideoSubmissionDisabledException();
    }

    private VideoSubmissionService RequireVideoService() => video ?? throw new VideoSubmissionDisabledException();

    public async Task RecordReceiptAsync(PhotoReceiptModel receipt, CancellationToken cancellationToken)
    {
        if (receipt.Bucket != settings.Bucket || !receipt.Key.StartsWith(TemporaryPrefix, StringComparison.Ordinal)) return;
        await using var transaction = await submissions.BeginAsync(cancellationToken);
        var submission = await photos.FindForObjectAsync(receipt.Bucket, receipt.Key, cancellationToken);
        if (submission is null) return;
        var now = clock.GetUtcNow();
        await photos.StoreReceiptAsync(submission.TemporaryObjectId, receipt, receipt.ReceivedAt.Add(CleanupDelay), cancellationToken);
        var kind = submission.PartSha256 is null ? "put" : "complete_multipart";
        if (submission.State == "receiving" && submission.TemporaryVersion is null && receipt.EventKind == kind
            && receipt.ReceivedAt >= Deadline(submission.StartedAt)
            && receipt.ReceivedAt <= Deadline(submission.CompletionDeadlineAt))
        {
            var files = await photos.FindFilesAsync(submission.RecordId, cancellationToken);
            var seal = files.All(file => file.FileId == submission.FileId || file.TemporaryVersion is not null);
            await photos.PinAsync(submission, receipt, now, seal, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var submission = await photos.FindWorkAsync(now, SubmissionService.Civil(now - CleanupDelay), cancellationToken);
        if (submission is null) return false;
        var lease = Guid.NewGuid();
        if (!await photos.TryLeaseAsync(submission.SessionId, submission.State, lease, now, now + LeaseDuration, cancellationToken))
        {
            return true;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(LeaseDuration - TimeSpan.FromMinutes(1));
        try
        {
            var files = await photos.FindFilesAsync(submission.RecordId, budget.Token);
            if (submission.State == "receiving")
            {
                await ReconcileExpiryAsync(submission, lease, files, budget.Token);
            }
            else
            {
                await ValidateAndConfirmAsync(submission, lease, files, budget.Token);
            }
        }
        catch (PhotoDecoderUnavailableException)
        {
            await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow() + RetryDelay, $"{submission.Format}-decoder-unavailable",
                cancellationToken);
        }
        catch (PhotoStorageUnavailableException)
        {
            await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow() + RetryDelay, $"{submission.Format}-storage-unavailable",
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow() + RetryDelay, $"{submission.Format}-work-timeout",
                cancellationToken);
        }
        return true;
    }

    public async Task<bool> CleanNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var cleanup = await photos.FindCleanupAsync(now, now - CleanupDelay, cancellationToken);
        if (cleanup is null)
        {
            return video is not null && await video.CleanMultipartAsync(now, CleanupDelay, cancellationToken);
        }
        await storage.DeleteAsync(cleanup.Bucket, cleanup.Key, cleanup.VersionId, cancellationToken);
        await photos.MarkDeletedAsync(cleanup, clock.GetUtcNow(), cancellationToken);
        return true;
    }

    private async Task ReconcileExpiryAsync(PhotoSubmissionEntity submission, Guid lease,
        IReadOnlyList<PhotoSubmissionEntity> files, CancellationToken cancellationToken)
    {
        var missing = files.Where(file => file.TemporaryVersion is null).ToArray();
        var late = false;
        foreach (var file in missing)
        {
            var videoService = file.Format == "video" ? RequireVideoService() : null;
            if (videoService is not null && !await videoService.AbortOpenUploadsAsync(file, cancellationToken))
            {
                await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow() + RetryDelay,
                    "video-multipart-abort-pending", cancellationToken);
                return;
            }

            var versions = await storage.ListVersionsAsync(file.Bucket, file.TemporaryKey, cancellationToken);
            foreach (var version in versions)
            {
                if (!await photos.HasReceiptAsync(file.TemporaryObjectId, version.VersionId, cancellationToken))
                {
                    await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow().AddHours(1),
                        $"{submission.Format}-receipt-missing", cancellationToken);
                    reporter?.ReportMissingReceipt(submission.CorrelationId);
                    return;
                }
                if (videoService is not null && await videoService.IsLateReceiptAsync(file, version.VersionId, cancellationToken))
                {
                    late = true;
                }
            }
        }

        const string expired = "submission-deadline-exceeded";
        await photos.TerminateAsync(submission, lease, late ? "rejected" : "expired", expired,
            missing.ToDictionary(file => file.FileId, _ => expired), clock.GetUtcNow(), cancellationToken);
    }

    private async Task ValidateAndConfirmAsync(PhotoSubmissionEntity submission, Guid lease,
        IReadOnlyList<PhotoSubmissionEntity> files, CancellationToken cancellationToken)
    {
        var validated = new List<ValidatedSubmissionFileModel>(files.Count);
        var rejected = new Dictionary<long, string>();
        string? rejectionCode = null;
        foreach (var file in files)
        {
            if (file.TemporaryVersion is not { } version || file.ReceiptAt is not { } receivedAt)
            {
                throw new PhotoStorageUnavailableException();
            }

            try
            {
                validated.Add(await ValidateAsync(file, version, receivedAt, cancellationToken));
            }
            catch (PhotoContentInvalidException)
            {
                rejectionCode = file.Format + "-invalid";
                rejected.Add(file.FileId, rejectionCode);
            }
            catch (PhotoDecoderBudgetException)
            {
                var code = file.Format + "-resource-budget";
                rejected.Add(file.FileId, code);
                rejectionCode ??= code;
            }
        }

        if (rejectionCode is not null)
        {
            await photos.TerminateAsync(submission, lease, "rejected", rejectionCode, rejected, clock.GetUtcNow(),
                cancellationToken);
            return;
        }

        var originals = new List<ConfirmPhotoFileModel>(files.Count);
        foreach (var file in validated)
        {
            var original = await PreserveAsync(file.File, file.Version, file.MediaType, cancellationToken);
            originals.Add(new ConfirmPhotoFileModel(file.File, file.Image, original, SubmissionService.Civil(file.ReceivedAt),
                file.Video));
        }

        await ConfirmAsync(submission, lease, validated.Max(file => file.ReceivedAt), originals, cancellationToken);
    }

    private async Task<ValidatedSubmissionFileModel> ValidateAsync(PhotoSubmissionEntity file, string version,
        DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        if (file.Format == "video")
        {
            var metadata = await RequireVideoService().ValidateAsync(file, version, cancellationToken);
            return new ValidatedSubmissionFileModel(file, version, receivedAt, metadata.MediaType, null, metadata);
        }

        var bytes = await storage.ReadAsync(file.Bucket, file.TemporaryKey, version, file.DeclaredSize, cancellationToken);
        if (!MatchesContent(bytes, file))
        {
            throw new PhotoContentInvalidException();
        }
        var image = await decoder.DecodeAsync(bytes, cancellationToken);
        if (!MatchesMediaType(file.DeclaredMediaType, image.MediaType))
        {
            throw new PhotoContentInvalidException();
        }
        return new ValidatedSubmissionFileModel(file, version, receivedAt, image.MediaType, image, null);
    }

    private async Task<PhotoObjectModel> PreserveAsync(PhotoSubmissionEntity submission, string sourceVersion, string mediaType,
        CancellationToken cancellationToken)
    {
        var versions = await storage.ListVersionsAsync(submission.Bucket, submission.OriginalKey, cancellationToken);
        if (versions.Count == 0)
        {
            var version = await storage.CopyAsync(submission.Bucket, submission.TemporaryKey, sourceVersion,
                submission.OriginalKey, mediaType, cancellationToken);
            return await CheckOriginalAsync(submission, sourceVersion, version, cancellationToken);
        }

        return await CheckOriginalAsync(submission, sourceVersion, versions[0].VersionId, cancellationToken);
    }

    private async Task<PhotoObjectModel> CheckOriginalAsync(PhotoSubmissionEntity submission, string sourceVersion, string version,
        CancellationToken cancellationToken)
    {
        var original = await storage.ReadMetadataAsync(submission.Bucket, submission.OriginalKey, version, cancellationToken);
        if (original is null || original.VersionId != version || original.Size != submission.DeclaredSize
            || original.SourceVersion != sourceVersion)
        {
            throw new PhotoStorageUnavailableException();
        }
        if (original.Sha256 is { } checksum && CryptographicOperations.FixedTimeEquals(checksum, submission.Sha256))
        {
            return original;
        }

        try
        {
            if (submission.Format == "video")
            {
                await RequireVideoService().VerifyOriginalAsync(submission, version, cancellationToken);
                return original;
            }

            var bytes = await storage.ReadAsync(submission.Bucket, submission.OriginalKey, version, submission.DeclaredSize,
                cancellationToken);
            return MatchesContent(bytes, submission) ? original : throw new PhotoStorageUnavailableException();
        }
        catch (PhotoContentInvalidException error)
        {
            throw new PhotoStorageUnavailableException(error);
        }
    }

    private async Task ConfirmAsync(PhotoSubmissionEntity submission, Guid lease, DateTimeOffset receivedAt,
        IReadOnlyList<ConfirmPhotoFileModel> files, CancellationToken cancellationToken)
    {
        await using var transaction = await submissions.BeginAsync(cancellationToken);
        var item = await photos.LockEventAsync(submission.EventId, cancellationToken)
            ?? throw new InvalidOperationException("Photo event is missing.");
        var correlation = submission.CorrelationId ?? Guid.NewGuid();
        var now = clock.GetUtcNow();
        var adjusted = now >= receivedAt ? now : receivedAt;
        var committedAt = new DateTimeOffset(adjusted.UtcTicks - adjusted.UtcTicks % 10, TimeSpan.Zero);
        var first = item.LockedAt is null && !await submissions.HasConfirmedAsync(item.Id, SubmissionService.FirstMessageId(item.Id),
            cancellationToken);
        var command = new ConfirmPhotoSubmissionCommand(submission, lease, files, SubmissionService.Civil(receivedAt),
            SubmissionService.Civil(committedAt), committedAt);
        if (!await photos.ConfirmAsync(command, cancellationToken)) return;
        if (first)
        {
            await submissions.InsertOutboxAsync(SubmissionService.FirstConfirmationMessage(item.Id, submission.RecordId,
                correlation, committedAt), cancellationToken);
        }
        foreach (var file in files)
        {
            await submissions.InsertOutboxAsync(MediaConfirmationMessage(file.File, correlation, committedAt), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static SubmissionOutboxEntity MediaConfirmationMessage(PhotoSubmissionEntity file, Guid correlation,
        DateTimeOffset committedAt)
    {
        var mediaId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            MediaConfirmation + ":" + file.FileId.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));
        var payload = JsonSerializer.Serialize(new
        {
            eventId = file.EventId.ToString(CultureInfo.InvariantCulture),
            recordId = file.RecordId.ToString(CultureInfo.InvariantCulture),
            fileId = file.FileId.ToString(CultureInfo.InvariantCulture),
            confirmedAt = committedAt
        });
        return new SubmissionOutboxEntity(mediaId, MediaConfirmation, file.EventId, payload, correlation, committedAt);
    }

    private static bool MatchesContent(byte[] bytes, PhotoSubmissionEntity submission) =>
        bytes.Length == submission.DeclaredSize && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), submission.Sha256);

    private static bool MatchesMediaType(string declared, string decoded) => declared == decoded
        || (declared is "image/heic" or "image/heif" && decoded is "image/heic" or "image/heif");

    internal static DateTimeOffset Deadline(DateTime value) => EventOpeningService.ToBrasiliaInstant(value).ToUniversalTime();
}
