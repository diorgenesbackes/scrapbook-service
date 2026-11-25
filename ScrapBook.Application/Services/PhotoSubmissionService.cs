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
    IPhotoSubmissionStorage storage, IPhotoSubmissionDecoder decoder, PhotoSubmissionSettings settings, TimeProvider clock)
{
    public const string TemporaryPrefix = "submissions/temporary/";
    private const string OriginalPrefix = "submissions/original/";
    private const string MediaConfirmation = "record.media-confirmed.v1";
    private const int MaximumSize = 20_000_000;
    private static readonly TimeSpan CleanupDelay = TimeSpan.FromHours(24);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(10);

    internal static (string? Text, string? Caption, PhotoManifestModel? File, string CanonicalPayload) NormalizePayload(
        CreateSubmissionRequest request)
    {
        if (request.Text is not null) throw new EventRequestValidationException("text");
        if (request.Files is not [{ } file] || file.FileKey == Guid.Empty || file.Position != 1)
        {
            throw new EventRequestValidationException("files");
        }
        if (file.DeclaredMediaType is not ("image/jpeg" or "image/png" or "image/heic" or "image/heif"))
        {
            throw new EventRequestValidationException("files[0].declaredMediaType");
        }
        if (file.SizeBytes is < 1 or > MaximumSize)
        {
            throw new EventRequestValidationException("files[0].sizeBytes");
        }
        var hash = new byte[32];
        if (file.Sha256 is not { Length: 44 } sha256 || !Convert.TryFromBase64String(sha256, hash, out var written)
            || written != hash.Length || Convert.ToBase64String(hash) != sha256)
        {
            throw new EventRequestValidationException("files[0].sha256");
        }

        var caption = SubmissionService.NormalizeLineEndings(request.Caption);
        if (caption is not null) SubmissionService.EnsureText(caption, 500, "caption", requireContent: false);
        var manifest = new PhotoManifestModel(file.FileKey, file.DeclaredMediaType, file.SizeBytes, hash);
        var canonicalPayload = "\0" + JsonSerializer.Serialize(new { request.Category, request.Format, caption, manifest });
        return (null, caption, manifest, canonicalPayload);
    }

    internal async Task<SubmissionStatusResponse> AdmitAsync(EventEntity item, long noticeId, string name, string? caption,
        PhotoManifestModel file, DateTimeOffset consentedAt, DateTimeOffset ingress, string operation, Guid attemptKey,
        string keyId, byte[] payloadHash, byte[] authorizationHash, Guid correlationId, CancellationToken cancellationToken)
    {
        var objectKey = Guid.NewGuid().ToString("N");
        var command = new CreatePhotoSubmissionCommand(item, noticeId, name, caption, file, SubmissionService.Civil(consentedAt),
            SubmissionService.Civil(ingress), operation, attemptKey, keyId, payloadHash, authorizationHash, ingress.AddDays(30),
            correlationId, settings.Bucket, TemporaryPrefix + objectKey, OriginalPrefix + objectKey);
        await photos.InsertAsync(command, cancellationToken);
        return new SubmissionStatusResponse("receiving", CompletionDeadlineAt: Deadline(item.CompletionDeadlineAt));
    }

    public async Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(long recordId, Guid fileKey, DateTimeOffset credentialExpiry,
        CancellationToken cancellationToken)
    {
        var submission = await photos.FindForRecordAsync(recordId, cancellationToken);
        if (submission is null || submission.FileKey != fileKey)
        {
            throw new SubmissionAttemptNotFoundException();
        }
        var now = clock.GetUtcNow();
        var expiresAt = Deadline(submission.CompletionDeadlineAt);
        if (expiresAt <= now) throw new SubmissionDeadlineExceededException();
        if (submission.State != "receiving") throw new PhotoUploadUnavailableException();
        if (credentialExpiry < expiresAt) expiresAt = credentialExpiry;
        var shortExpiry = now.Add(AuthorizationLifetime);
        if (shortExpiry < expiresAt) expiresAt = shortExpiry;
        return await storage.AuthorizeAsync(submission.Bucket, submission.TemporaryKey, submission.DeclaredMediaType,
            submission.DeclaredSize, submission.Sha256, expiresAt, cancellationToken);
    }

    public async Task RecordReceiptAsync(PhotoReceiptModel receipt, CancellationToken cancellationToken)
    {
        if (receipt.Bucket != settings.Bucket || !receipt.Key.StartsWith(TemporaryPrefix, StringComparison.Ordinal)) return;
        await using var transaction = await submissions.BeginAsync(cancellationToken);
        var submission = await photos.FindForObjectAsync(receipt.Bucket, receipt.Key, cancellationToken);
        if (submission is null) return;
        var now = clock.GetUtcNow();
        await photos.StoreReceiptAsync(submission.TemporaryObjectId, receipt, receipt.ReceivedAt.Add(CleanupDelay), cancellationToken);
        if (submission.State == "receiving" && submission.TemporaryVersion is null
            && receipt.ReceivedAt >= Deadline(submission.StartedAt)
            && receipt.ReceivedAt <= Deadline(submission.CompletionDeadlineAt))
        {
            await photos.PinAsync(submission, receipt, now, cancellationToken);
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
            if (submission.TemporaryVersion is null)
            {
                await ReconcileExpiryAsync(submission, lease, budget.Token);
            }
            else
            {
                await ValidateAndConfirmAsync(submission, lease, budget.Token);
            }
        }
        catch (PhotoContentInvalidException)
        {
            await photos.TerminateAsync(submission, lease, "rejected", "photo-invalid", clock.GetUtcNow(), cancellationToken);
        }
        catch (PhotoDecoderBudgetException)
        {
            await photos.TerminateAsync(submission, lease, "rejected", "photo-resource-budget", clock.GetUtcNow(), cancellationToken);
        }
        catch (PhotoDecoderUnavailableException)
        {
            await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow() + RetryDelay, "photo-decoder-unavailable",
                cancellationToken);
        }
        catch (PhotoStorageUnavailableException)
        {
            await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow() + RetryDelay, "photo-storage-unavailable",
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow() + RetryDelay, "photo-work-timeout",
                cancellationToken);
        }
        return true;
    }

    public async Task<bool> CleanNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var cleanup = await photos.FindCleanupAsync(now, now - CleanupDelay, cancellationToken);
        if (cleanup is null) return false;
        await storage.DeleteAsync(cleanup.Bucket, cleanup.Key, cleanup.VersionId, cancellationToken);
        await photos.MarkDeletedAsync(cleanup, clock.GetUtcNow(), cancellationToken);
        return true;
    }

    private async Task ReconcileExpiryAsync(PhotoSubmissionEntity submission, Guid lease, CancellationToken cancellationToken)
    {
        var versions = await storage.ListVersionsAsync(submission.Bucket, submission.TemporaryKey, cancellationToken);
        foreach (var version in versions)
        {
            if (!await photos.HasReceiptAsync(submission.TemporaryObjectId, version.VersionId, cancellationToken))
            {
                await photos.ScheduleAsync(submission.SessionId, lease, clock.GetUtcNow().AddHours(1),
                    "photo-receipt-missing", cancellationToken);
                return;
            }
        }

        await photos.TerminateAsync(submission, lease, "expired", "submission-deadline-exceeded", clock.GetUtcNow(),
            cancellationToken);
    }

    private async Task ValidateAndConfirmAsync(PhotoSubmissionEntity submission, Guid lease, CancellationToken cancellationToken)
    {
        var sourceVersion = submission.TemporaryVersion ?? throw new PhotoStorageUnavailableException();
        var receivedAt = submission.ReceiptAt ?? throw new PhotoStorageUnavailableException();
        var bytes = await storage.ReadAsync(submission.Bucket, submission.TemporaryKey, sourceVersion,
            submission.DeclaredSize, cancellationToken);
        if (!MatchesContent(bytes, submission))
        {
            throw new PhotoContentInvalidException();
        }
        var image = await decoder.DecodeAsync(bytes, cancellationToken);
        if (!MatchesMediaType(submission.DeclaredMediaType, image.MediaType))
        {
            throw new PhotoContentInvalidException();
        }

        var original = await PreserveAsync(submission, sourceVersion, image.MediaType, cancellationToken);
        await ConfirmAsync(submission, lease, receivedAt, image, original, cancellationToken);
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
            var bytes = await storage.ReadAsync(submission.Bucket, submission.OriginalKey, version, submission.DeclaredSize,
                cancellationToken);
            return MatchesContent(bytes, submission) ? original : throw new PhotoStorageUnavailableException();
        }
        catch (PhotoContentInvalidException error)
        {
            throw new PhotoStorageUnavailableException(error);
        }
    }

    private async Task ConfirmAsync(PhotoSubmissionEntity submission, Guid lease, DateTimeOffset receivedAt, PhotoImageModel image,
        PhotoObjectModel original, CancellationToken cancellationToken)
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
        var command = new ConfirmPhotoSubmissionCommand(submission, lease, image, original, SubmissionService.Civil(receivedAt),
            SubmissionService.Civil(committedAt), committedAt);
        if (!await photos.ConfirmAsync(command, cancellationToken)) return;
        if (first)
        {
            await submissions.InsertOutboxAsync(SubmissionService.FirstConfirmationMessage(item.Id, submission.RecordId,
                correlation, committedAt), cancellationToken);
        }
        var mediaId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            MediaConfirmation + ":" + submission.FileId.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));
        var payload = JsonSerializer.Serialize(new
        {
            eventId = item.Id.ToString(CultureInfo.InvariantCulture),
            recordId = submission.RecordId.ToString(CultureInfo.InvariantCulture),
            fileId = submission.FileId.ToString(CultureInfo.InvariantCulture),
            confirmedAt = committedAt
        });
        await submissions.InsertOutboxAsync(new SubmissionOutboxEntity(mediaId, MediaConfirmation, item.Id, payload,
            correlation, committedAt), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool MatchesContent(byte[] bytes, PhotoSubmissionEntity submission) =>
        bytes.Length == submission.DeclaredSize && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), submission.Sha256);

    private static bool MatchesMediaType(string declared, string decoded) => declared == decoded
        || (declared is "image/heic" or "image/heif" && decoded is "image/heic" or "image/heif");

    private static DateTimeOffset Deadline(DateTime value) => EventOpeningService.ToBrasiliaInstant(value).ToUniversalTime();
}
