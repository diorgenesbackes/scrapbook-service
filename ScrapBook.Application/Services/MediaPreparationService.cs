using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Application.Diagnostics;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.MediaConfirmationService;

namespace ScrapBook.Application.Services;

public sealed class MediaPreparationService(IMediaPreparationRepository repository, IMediaVariantStorage storage,
    IPhotoVariantProcessor processor, TimeProvider clock, MediaProcessingTelemetry? telemetry = null)
{
    private const int MaximumAttempts = 5;
    private const int MaximumOriginalBytes = 20_000_000;
    private static readonly int[] RetrySeconds = [5, 15, 30, 60];
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PreparationBudget = TimeSpan.FromSeconds(75);

    public async Task<MediaFileProjectionModel?> GetFileAsync(long eventId, long recordId, long fileId,
        CancellationToken cancellationToken)
    {
        var file = await repository.GetFileAsync(eventId, recordId, fileId, cancellationToken);
        if (file is null) return null;
        if (file.State == "available" && HasAllReadyVariants(file)) return file;

        var state = file.State switch
        {
            "not_requested" => "pending",
            "available" => "failed",
            _ => file.State
        };
        return file with { State = state, Variants = [] };
    }

    private static bool HasAllReadyVariants(MediaFileProjectionModel file) => file.Kind switch
    {
        "photo" => file.Variants is [{ Kind: "image" }, { Kind: "thumbnail" }],
        "audio" or "video" => file.Variants is [{ Kind: "playback" }],
        _ => false
    };

    public Task<bool> PrepareNextAsync(CancellationToken cancellationToken) =>
        PrepareNextAsync(true, cancellationToken);

    public Task<bool> PrepareNextAsync(bool allowHistorical, CancellationToken cancellationToken) =>
        PrepareNextAsync(allowHistorical, false, cancellationToken);

    public async Task<bool> PrepareNextAsync(bool allowHistorical, bool preferHistorical, CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(allowHistorical, preferHistorical, cancellationToken);
        if (claim.Work is { } work)
        {
            await PrepareAsync(work, cancellationToken);
        }
        return claim.Found;
    }

    private async Task<(bool Found, PhotoPreparationWorkModel? Work)> ClaimAsync(bool allowHistorical, bool preferHistorical,
        CancellationToken cancellationToken)
    {
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var historicalFirst = allowHistorical && preferHistorical;
        var job = await repository.FindEligiblePhotoAsync(now, historicalFirst, cancellationToken);
        if (job is null && allowHistorical)
        {
            job = await repository.FindEligiblePhotoAsync(now, !historicalFirst, cancellationToken);
        }
        if (job is null) return (false, null);

        if (job.ActiveAttemptId is { } interrupted)
        {
            await repository.EndAttemptAsync(interrupted, "failed", "MEDIA_ATTEMPT_INTERRUPTED", now, cancellationToken);
        }
        if (job.AttemptCount >= MaximumAttempts)
        {
            await FailPermanentlyAsync(job, "prepare", "MEDIA_ATTEMPTS_EXHAUSTED", now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            telemetry?.RecordFailure("photo", job.IsHistorical, "prepare");
            return (true, null);
        }
        var confirmation = Confirmation(job);
        var input = await repository.FindSourceAsync(confirmation, true, cancellationToken);
        if (!MatchesSource(confirmation, input) || input is not { Kind: "photo", Size: <= MaximumOriginalBytes, ProcessingState: "pending" or "processing" })
        {
            await FailPermanentlyAsync(job, "prepare", "MEDIA_SOURCE_INTEGRITY", now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            telemetry?.RecordFailure("photo", job.IsHistorical, "prepare");
            return (true, null);
        }

        var lease = Guid.NewGuid();
        var requestKey = Guid.NewGuid();
        var prefix = FormattableString.Invariant($"submissions/derived/{job.FileId}/{requestKey:N}/");
        var attempt = await repository.StartPhotoAsync(job, input.Bucket, lease, now + LeaseDuration, requestKey, prefix,
            job.LastAttemptNumber + 1, PhotoKinds, now, cancellationToken);
        var work = new PhotoPreparationWorkModel(job with { Version = job.Version + 1, AttemptCount = job.AttemptCount + 1 },
            input, attempt, lease, prefix);
        await transaction.CommitAsync(cancellationToken);
        if (job.ActiveAttemptId is not null)
        {
            telemetry?.Record(MediaProcessingStage.Reconciliation, MediaProcessingOutcome.Interrupted, "photo", job.IsHistorical);
        }
        if (job.AttemptCount == 0) telemetry?.RecordFirstClaim(job, "photo", clock.GetUtcNow());
        return (true, work);
    }

    private async Task PrepareAsync(PhotoPreparationWorkModel work, CancellationToken cancellationToken)
    {
        using var activity = MediaProcessingTelemetry.StartActivity(MediaProcessingStage.Preparation, ActivityKind.Internal);
        var started = clock.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PreparationBudget);
        IReadOnlyList<PreparedPhotoVariantModel> results;
        try
        {
            var profile = ReadProfile(work.Job);
            var original = await storage.ReadOriginalAsync(work.Source, budget.Token);
            if (original.LongLength != work.Source.Size
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), work.Source.Sha256))
            {
                throw new MediaSourceIntegrityException();
            }
            var variants = await processor.PrepareAsync(original, profile, budget.Token);
            CheckVariant(variants.Image, variants, profile.ImageMaximumSide);
            CheckVariant(variants.Thumbnail, variants, profile.ThumbnailMaximumSide);
            results = [
                await StoreAsync(work, "image", variants.Image, budget.Token),
                await StoreAsync(work, "thumbnail", variants.Thumbnail, budget.Token)
            ];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            await FailAttemptAsync(work, "prepare", "MEDIA_PREPARATION_TIMEOUT", true, started, cancellationToken);
            return;
        }
        catch (Exception error) when (error is MediaSourceIntegrityException or PhotoContentInvalidException or PhotoDecoderBudgetException
            or MediaOutputInvalidException or PhotoDecoderUnavailableException or PhotoStorageUnavailableException or MediaVariantStorageException)
        {
            var (stage, code, retryable) = error switch
            {
                MediaSourceIntegrityException => ("prepare", "MEDIA_SOURCE_INTEGRITY", false),
                PhotoContentInvalidException => ("prepare", "MEDIA_SOURCE_INVALID", false),
                PhotoDecoderBudgetException => ("prepare", "MEDIA_DECODER_BUDGET", true),
                PhotoDecoderUnavailableException => ("prepare", "MEDIA_DECODER_UNAVAILABLE", true),
                PhotoStorageUnavailableException => ("prepare", "MEDIA_SOURCE_UNAVAILABLE", true),
                MediaOutputInvalidException => ("verify", "MEDIA_VARIANT_INVALID", false),
                _ => ("verify", "MEDIA_STORAGE_UNAVAILABLE", true)
            };
            await FailAttemptAsync(work, stage, code, retryable, started, cancellationToken);
            return;
        }

        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (!await repository.RetainPhotoLeaseAsync(work, now, cancellationToken)) return;
        foreach (var result in results)
        {
            await repository.PromoteVariantAsync(work, result, now, cancellationToken);
        }
        await repository.EndAttemptAsync(work.AttemptId, "succeeded", null, now, cancellationToken);
        await repository.SetPhotoStateAsync(work.Job, "completed", "available", null, null, null, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        telemetry?.RecordReady(work.Job, "photo", clock.GetUtcNow());
        telemetry?.Record(MediaProcessingStage.Preparation, MediaProcessingOutcome.Available, "photo", work.Job.IsHistorical,
            clock.GetElapsedTime(started).TotalSeconds);
    }

    private async Task<PreparedPhotoVariantModel> StoreAsync(PhotoPreparationWorkModel work, string kind,
        PhotoVariantModel variant, CancellationToken cancellationToken)
    {
        var key = work.OutputPrefix + kind + ".jpg";
        var result = await storage.StoreVerifiedAsync(work.Source.Bucket, key, variant, cancellationToken);
        if (result.Bucket != work.Source.Bucket || result.Key != key || !IsVersioned(result.VersionId)
            || result.Size != variant.Bytes.LongLength || result.LastModifiedAt == default
            || !CryptographicOperations.FixedTimeEquals(result.Sha256, SHA256.HashData(variant.Bytes)))
        {
            throw new MediaOutputInvalidException();
        }
        return new PreparedPhotoVariantModel(kind, variant.Width, variant.Height, result);
    }

    private async Task FailAttemptAsync(PhotoPreparationWorkModel work, string stage, string code,
        bool retryable, long started, CancellationToken cancellationToken)
    {
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (!await repository.RetainPhotoLeaseAsync(work, now, cancellationToken)) return;

        await repository.EndAttemptAsync(work.AttemptId, "failed", code, now, cancellationToken);
        var retry = retryable && work.Job.AttemptCount < MaximumAttempts;
        if (retry)
        {
            var seconds = RetrySeconds[work.Job.AttemptCount - 1];
            var retryAt = now.AddSeconds(seconds + Random.Shared.NextDouble());
            await repository.SetPhotoStateAsync(work.Job, "pending", "processing", null, code, retryAt, now, cancellationToken);
        }
        else
        {
            await FailPermanentlyAsync(work.Job, stage, retryable ? "MEDIA_ATTEMPTS_EXHAUSTED" : code, now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        telemetry?.Record(MediaProcessingStage.Preparation, retry ? MediaProcessingOutcome.RetryScheduled : MediaProcessingOutcome.Failed,
            "photo", work.Job.IsHistorical, clock.GetElapsedTime(started).TotalSeconds);
        if (!retry) telemetry?.RecordFailure("photo", work.Job.IsHistorical, stage);
    }

    private async Task FailPermanentlyAsync(MediaJobEntity job, string stage, string code, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await repository.SetPhotoStateAsync(job, "failed", "failed", "failed", code, null, now, cancellationToken);
        await repository.InsertFailureAsync(MediaFailureService.Failed(Confirmation(job), job.ProfileVersion, job.RetryCycle,
            stage, code, now), cancellationToken);
    }

    private static PhotoPreparationProfileModel ReadProfile(MediaJobEntity job)
    {
        try
        {
            return JsonSerializer.Deserialize<PhotoPreparationProfileModel>(job.Profile) is { } profile && profile == PhotoProfile
                ? profile : throw new MediaOutputInvalidException();
        }
        catch (JsonException)
        {
            throw new MediaOutputInvalidException();
        }
    }

    private static void CheckVariant(PhotoVariantModel variant, PhotoVariantSetModel variants, int maximum)
    {
        if (variants.SourceWidth <= 0 || variants.SourceHeight <= 0 || variant.Width <= 0 || variant.Height <= 0
            || variant.Width > variants.SourceWidth || variant.Height > variants.SourceHeight
            || variant.Bytes.Length < 4 || variant.Bytes[0] != 0xff || variant.Bytes[1] != 0xd8
            || variant.Bytes[^2] != 0xff || variant.Bytes[^1] != 0xd9)
        {
            throw new MediaOutputInvalidException();
        }
        var scale = Math.Min(1, (double)maximum / Math.Max(variants.SourceWidth, variants.SourceHeight));
        if (Math.Max(variant.Width, variant.Height) > maximum || Math.Abs(variant.Width - variants.SourceWidth * scale) > 1
            || Math.Abs(variant.Height - variants.SourceHeight * scale) > 1)
        {
            throw new MediaOutputInvalidException();
        }
    }
}
