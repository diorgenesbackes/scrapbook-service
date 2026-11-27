using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.MediaConfirmationService;

namespace ScrapBook.Application.Services;

public sealed class MediaAudioPreparationService(IMediaAudioRepository repository, IMediaAudioStorage storage,
    IMediaTranscodingProvider provider, MediaAudioSettings settings, TimeProvider clock)
{
    private const int MaximumAttempts = 5;
    private const int MaximumOriginalBytes = 50_000_000;
    private const int MaximumStagingBytes = 64_000_000;
    private const int MaximumPlaybackBytes = 10_000_000;
    private const int SampleRate = 48_000;
    private static readonly int[] RetrySeconds = [5, 15, 30, 60];
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LocalBudget = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ProviderBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SubmitWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReconciliationHorizon = TimeSpan.FromHours(24);
    private static readonly Meter Meter = new("ScrapBook.MediaAudio");
    private static readonly Counter<long> Interventions = Meter.CreateCounter<long>("scrapbook.media.audio.intervention");

    public async Task<bool> PrepareNextAsync(CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(cancellationToken);
        if (claim.Work is { } work)
        {
            await AdvanceAsync(work, cancellationToken);
        }
        return claim.Found;
    }

    public async Task<bool> WakeAsync(MediaAudioCallbackModel callback, CancellationToken cancellationToken)
    {
        if (callback.Account != settings.Account || callback.Region != settings.Region
            || callback.RequestKey == Guid.Empty || !ValidJobId(callback.JobId)
            || callback.State is not ("complete" or "error" or "canceled"))
        {
            return false;
        }
        var target = await repository.FindAudioWakeAsync(callback.RequestKey, cancellationToken);
        if (target is null) return false;
        var plan = ReadPlan(target.Profile);
        if (plan.Settings.Account != callback.Account || plan.Settings.Region != callback.Region
            || plan.Settings.QueueArn != callback.QueueArn
            || target.ProviderJobId is { } previous && previous != callback.JobId)
        {
            return false;
        }
        var job = await GetJobAsync(callback.JobId, cancellationToken);
        if (job is null) throw new MediaProviderUnavailableException();
        if (!MatchesProvider(job, callback.RequestKey, callback.QueueArn, callback.JobId)) return false;

        var externalId = $"{callback.Account}:{callback.Region}:{callback.JobId}:{callback.State}";
        var payloadHash = Hash($"{externalId}:{callback.QueueArn}:{callback.RequestKey:N}");
        return await repository.WakeAudioAsync(target, callback.RequestKey, job.Id,
            ProviderHash(plan.Settings, job.Id), externalId, Hash(externalId), payloadHash, clock.GetUtcNow(), cancellationToken);
    }

    private async Task<(bool Found, MediaAudioWorkModel? Work)> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var job = await repository.FindEligibleAudioAsync(now, cancellationToken);
        if (job is null) return (false, null);

        var confirmation = Confirmation(job);
        var source = await repository.FindSourceAsync(confirmation, true, cancellationToken);
        if (!MatchesSource(confirmation, source)
            || source is not { Kind: "audio", Size: <= MaximumOriginalBytes, ProcessingState: "pending" or "processing" })
        {
            source = null;
        }

        var lease = Guid.NewGuid();
        var leaseUntil = now + LeaseDuration;
        MediaAudioAttemptEntity attempt;
        MediaAudioPlanModel plan;
        if (job.ActiveAttemptId is { } attemptId)
        {
            attempt = await repository.FindAudioAttemptAsync(job.Id, attemptId, cancellationToken)
                ?? throw new MediaPreparationWriteException();
            plan = ReadPlan(job.Profile);
            await repository.ResumeAudioAsync(job, lease, leaseUntil, now, cancellationToken);
        }
        else
        {
            if (source is null || job.AttemptCount >= MaximumAttempts)
            {
                var errorCode = source is null ? "MEDIA_SOURCE_INTEGRITY" : "MEDIA_ATTEMPTS_EXHAUSTED";
                await FailPermanentlyAsync(job, "prepare", errorCode, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (true, null);
            }
            var requestKey = Guid.NewGuid();
            var prefix = OutputPrefix(job.FileId, requestKey);
            plan = new MediaAudioPlanModel(settings, source.Bucket, null, null);
            var number = job.LastAttemptNumber + 1;
            var newId = await repository.StartAudioAsync(job, JsonSerializer.Serialize(plan), plan.Bucket, lease, leaseUntil,
                requestKey, InputKey(job.FileId, requestKey), prefix, prefix + "playback.m4a", number, now, cancellationToken);
            attempt = new MediaAudioAttemptEntity(newId, requestKey, prefix, now, null, null, "created");
            job = job with { AttemptCount = job.AttemptCount + 1, LastAttemptNumber = number, ActiveAttemptId = newId };
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, new(job with { Version = job.Version + 1 }, source, attempt, lease, leaseUntil, plan));
    }

    private async Task AdvanceAsync(MediaAudioWorkModel work, CancellationToken cancellationToken)
    {
        if (work.Plan.Settings.Account != settings.Account || work.Plan.Settings.Region != settings.Region)
        {
            await DeferAsync(work, null, "MEDIA_PROVIDER_SCOPE_CHANGED", cancellationToken);
            return;
        }
        if (work.Plan.Input is null)
        {
            var staged = await StageAsync(work, cancellationToken);
            if (staged is null) return;
            work = staged;
        }
        if (!MatchesPlan(work))
        {
            await DeferAsync(work, null, "MEDIA_INPUT_RECONCILIATION_REQUIRED", cancellationToken);
            return;
        }
        var request = new MediaAudioRequestModel(work.Attempt.RequestKey, work.Plan.Bucket,
            work.Plan.Input!.Object.Key, work.Attempt.OutputPrefix + "playback.m4a", work.Plan.Profile!);
        if (work.Attempt.ProviderJobId is { } jobId)
        {
            await ReconcileKnownAsync(work, request, jobId, cancellationToken);
        }
        else if (work.Attempt.SubmittedAt is { } submittedAt)
        {
            await ReconcileUnknownAsync(work, request, submittedAt, cancellationToken);
        }
        else
        {
            await SubmitAsync(work, request, cancellationToken);
        }
    }

    private async Task<MediaAudioWorkModel?> StageAsync(MediaAudioWorkModel work, CancellationToken cancellationToken)
    {
        if (work.Attempt.SubmittedAt is not null || work.Attempt.ProviderJobId is not null)
        {
            await DeferAsync(work, null, "MEDIA_INPUT_RECONCILIATION_REQUIRED", cancellationToken);
            return null;
        }
        if (work.Source is not { } source || source.Bucket != work.Plan.Bucket)
        {
            await FailAttemptAsync(work, "prepare", "MEDIA_SOURCE_INTEGRITY", false, cancellationToken);
            return null;
        }
        var key = InputKey(work.Job.FileId, work.Attempt.RequestKey);
        MediaAudioInputModel input;
        using var timeout = new CancellationTokenSource(LocalBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            input = await storage.StageAsync(source, key, linked.Token);
            if (!ValidObject(input.Object, work.Plan.Bucket, key, MaximumStagingBytes)
                || input.Duration is <= 0 or > 300 || input.Channels is not (1 or 2))
            {
                throw new MediaOutputInvalidException();
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await FailAttemptAsync(work, "prepare", "MEDIA_PREPARATION_TIMEOUT", true, cancellationToken);
            return null;
        }
        catch (MediaVariantStorageException)
        {
            await FailAttemptAsync(work, "prepare", "MEDIA_SOURCE_UNAVAILABLE", true, cancellationToken);
            return null;
        }
        catch (Exception error) when (error is MediaSourceIntegrityException or MediaOutputInvalidException)
        {
            var code = error is MediaSourceIntegrityException ? "MEDIA_SOURCE_INTEGRITY" : "MEDIA_SOURCE_INVALID";
            await FailAttemptAsync(work, "prepare", code, false, cancellationToken);
            return null;
        }

        var plan = work.Plan with { Input = input, Profile = EffectiveProfile(work.Plan.Settings, input.Channels) };
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (!await repository.RetainAudioLeaseAsync(work, now, cancellationToken)) return null;
        await repository.SetAudioInputAsync(work, input.Object, JsonSerializer.Serialize(plan), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return work with { Plan = plan };
    }

    private async Task SubmitAsync(MediaAudioWorkModel work, MediaAudioRequestModel request, CancellationToken cancellationToken)
    {
        var startedAt = await AuthorizeSubmissionAsync(work, cancellationToken);
        if (startedAt is null) return;

        var firstSubmission = work.Attempt.SubmittedAt is null;
        work = work with { Attempt = work.Attempt with { SubmittedAt = startedAt, State = "unknown" } };
        using var timeout = new CancellationTokenSource(ProviderBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        MediaProviderJobModel job;
        try
        {
            var now = clock.GetUtcNow();
            if (!CanSubmit(now, work.LeaseUntil, startedAt.Value))
            {
                await DeferAsync(work, null, "MEDIA_SUBMISSION_UNKNOWN", cancellationToken);
                return;
            }
            linked.Token.ThrowIfCancellationRequested();
            job = await provider.CreateAsync(request, linked.Token);
        }
        catch (MediaJobRejectedException error) when (firstSubmission)
        {
            await FailAttemptAsync(work, "transcode", "MEDIA_PROVIDER_REJECTED", error.Retryable, cancellationToken);
            return;
        }
        catch (Exception error) when (error is MediaJobRejectedException or MediaProviderUnavailableException
            || error is OperationCanceledException && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await DeferAsync(work, null, "MEDIA_SUBMISSION_UNKNOWN", cancellationToken);
            return;
        }

        await RecordProviderAsync(work, request, job, cancellationToken);
    }

    private async Task<DateTimeOffset?> AuthorizeSubmissionAsync(MediaAudioWorkModel work, CancellationToken cancellationToken)
    {
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var startedAt = work.Attempt.SubmittedAt ?? now;
        if (!CanSubmit(now, work.LeaseUntil, startedAt)
            || !await repository.RetainAudioLeaseAsync(work, now + ProviderBudget, cancellationToken))
        {
            return null;
        }
        if (work.Attempt.SubmittedAt is null)
        {
            await repository.MarkAudioSubmitStartedAsync(work.Attempt.Id, now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return startedAt;
    }

    private async Task ReconcileUnknownAsync(MediaAudioWorkModel work, MediaAudioRequestModel request,
        DateTimeOffset submittedAt, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ProviderBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        MediaProviderJobModel? job;
        try
        {
            job = await provider.FindAsync(request, submittedAt, linked.Token);
        }
        catch (Exception error) when (error is MediaProviderUnavailableException
            || error is OperationCanceledException && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await DeferAsync(work, null, "MEDIA_PROVIDER_LOOKUP_UNAVAILABLE", cancellationToken);
            return;
        }
        if (job is not null)
        {
            await RecordProviderAsync(work, request, job, cancellationToken);
        }
        else if (CanSubmit(clock.GetUtcNow(), work.LeaseUntil, submittedAt))
        {
            await SubmitAsync(work, request, cancellationToken);
        }
        else
        {
            await DeferAsync(work, null, "MEDIA_SUBMISSION_UNKNOWN", cancellationToken);
        }
    }

    private async Task ReconcileKnownAsync(MediaAudioWorkModel work, MediaAudioRequestModel request, string jobId,
        CancellationToken cancellationToken)
    {
        MediaProviderJobModel? job;
        try
        {
            job = await GetJobAsync(jobId, cancellationToken);
        }
        catch (MediaProviderUnavailableException)
        {
            await DeferAsync(work, null, "MEDIA_PROVIDER_LOOKUP_UNAVAILABLE", cancellationToken);
            return;
        }
        if (job is null || !MatchesProvider(job, request.RequestKey, request.Profile.QueueArn, jobId))
        {
            await DeferAsync(work, null, "MEDIA_PROVIDER_RESULT_UNVERIFIED", cancellationToken);
            return;
        }
        switch (job.State)
        {
            case "complete":
                await VerifyAndPromoteAsync(work, request, job, cancellationToken);
                break;
            case "error" or "canceled":
                await FailAttemptAsync(work, "transcode", "MEDIA_PROVIDER_JOB_FAILED", true, cancellationToken);
                break;
            default:
                await DeferAsync(work, job, null, cancellationToken);
                break;
        }
    }

    private async Task<MediaProviderJobModel?> GetJobAsync(string jobId, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ProviderBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await provider.GetAsync(jobId, linked.Token);
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new MediaProviderUnavailableException(error);
        }
    }

    private Task RecordProviderAsync(MediaAudioWorkModel work, MediaAudioRequestModel request, MediaProviderJobModel job,
        CancellationToken cancellationToken) =>
        MatchesProvider(job, request.RequestKey, request.Profile.QueueArn, work.Attempt.ProviderJobId)
            ? DeferAsync(work, job, null, cancellationToken)
            : DeferAsync(work, null, "MEDIA_PROVIDER_RESULT_UNVERIFIED", cancellationToken);

    private async Task VerifyAndPromoteAsync(MediaAudioWorkModel work, MediaAudioRequestModel request, MediaProviderJobModel job,
        CancellationToken cancellationToken)
    {
        MediaAudioVariantModel variant;
        using var timeout = new CancellationTokenSource(LocalBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            if (job.OutputUri is { } output && output != $"s3://{request.Bucket}/{request.OutputKey}")
            {
                throw new MediaOutputInvalidException();
            }
            variant = await storage.VerifyAsync(request.Bucket, request.OutputKey, linked.Token);
            if (!ValidObject(variant.Object, request.Bucket, request.OutputKey, MaximumPlaybackBytes)
                || variant.MediaType != "audio/mp4" || variant.Container != "m4a" || variant.AudioCodec != "aac"
                || variant.Channels != request.Profile.Channels || variant.SampleRate != request.Profile.SampleRate
                || variant.Duration <= 0 || Math.Abs(variant.Duration - work.Plan.Input!.Duration) > 0.250m)
            {
                throw new MediaOutputInvalidException();
            }
        }
        catch (MediaOutputInvalidException)
        {
            await FailAttemptAsync(work, "verify", "MEDIA_VARIANT_INVALID", false, cancellationToken);
            return;
        }
        catch (Exception error) when (error is MediaVariantStorageException
            || error is OperationCanceledException && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await DeferAsync(work, job, "MEDIA_VARIANT_STORAGE_UNAVAILABLE", cancellationToken);
            return;
        }

        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (!await repository.RetainAudioLeaseAsync(work, now, cancellationToken)) return;
        await repository.PromoteAudioAsync(work, variant, now, cancellationToken);
        await repository.SetAudioAttemptAsync(work.Attempt.Id, "succeeded", job.Id, ProviderHash(work.Plan.Settings, job.Id),
            null, now, now, cancellationToken);
        await repository.SetAudioStateAsync(work.Job, "completed", "available", null, null, null, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeferAsync(MediaAudioWorkModel work, MediaProviderJobModel? providerJob, string? code,
        CancellationToken cancellationToken)
    {
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (!await repository.RetainAudioLeaseAsync(work, now, cancellationToken)) return;

        var age = now - (work.Attempt.SubmittedAt ?? work.Attempt.StartedAt);
        var intervention = age >= ReconciliationHorizon;
        var next = now + ReconciliationDelay(age);
        if (providerJob?.State is "complete" or "error" or "canceled" && code is null)
        {
            next = now;
        }
        else if (providerJob is null && work.Attempt.ProviderJobId is null && work.Attempt.SubmittedAt is { } start
            && CanSubmit(now.AddSeconds(5), now + LeaseDuration, start))
        {
            next = now.AddSeconds(5);
        }
        var state = providerJob?.State switch
        {
            "submitted" => "submitted",
            null => work.Attempt.State,
            _ => "running"
        };
        var jobState = code is null && providerJob?.State is ("submitted" or "progressing") ? "running" : "reconciling";
        var providerId = providerJob?.Id;
        var providerHash = providerId is null ? null : ProviderHash(work.Plan.Settings, providerId);
        var errorCode = intervention ? "MEDIA_RECONCILIATION_INTERVENTION_REQUIRED" : code;
        await repository.SetAudioAttemptAsync(work.Attempt.Id, state, providerId, providerHash, errorCode, null, now, cancellationToken);
        await repository.ReleaseAudioLeaseAsync(work, jobState, errorCode, next, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (intervention) Interventions.Add(1);
    }

    private async Task FailAttemptAsync(MediaAudioWorkModel work, string stage, string code, bool retryable,
        CancellationToken cancellationToken)
    {
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (!await repository.RetainAudioLeaseAsync(work, now, cancellationToken)) return;
        var providerId = work.Attempt.ProviderJobId;
        await repository.SetAudioAttemptAsync(work.Attempt.Id, "failed", providerId,
            providerId is null ? null : ProviderHash(work.Plan.Settings, providerId), code, now, now, cancellationToken);
        if (retryable && work.Job.AttemptCount < MaximumAttempts)
        {
            var next = now.AddSeconds(RetrySeconds[work.Job.AttemptCount - 1] + Random.Shared.NextDouble());
            await repository.SetAudioStateAsync(work.Job, "pending", "processing", null, code, next, now, cancellationToken);
        }
        else
        {
            await FailPermanentlyAsync(work.Job, stage, retryable ? "MEDIA_ATTEMPTS_EXHAUSTED" : code, now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FailPermanentlyAsync(MediaJobEntity job, string stage, string code, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await repository.SetAudioStateAsync(job, "failed", "failed", "failed", code, null, now, cancellationToken);
        await repository.InsertFailureAsync(MediaFailureService.Failed(Confirmation(job), job.ProfileVersion,
            job.RetryCycle, stage, code, now), cancellationToken);
    }

    private static MediaAudioPlanModel ReadPlan(string profile)
    {
        try
        {
            return JsonSerializer.Deserialize<MediaAudioPlanModel>(profile) is { Settings: not null, Bucket.Length: > 0 } plan
                && (plan.Input is null) == (plan.Profile is null)
                ? plan : throw new MediaPreparationWriteException();
        }
        catch (JsonException)
        {
            throw new MediaPreparationWriteException();
        }
    }

    private static bool MatchesPlan(MediaAudioWorkModel work) =>
        work.Plan.Input is { Duration: > 0 and <= 300, Channels: 1 or 2 } input
        && work.Plan.Profile == EffectiveProfile(work.Plan.Settings, input.Channels)
        && work.Attempt.OutputPrefix == OutputPrefix(work.Job.FileId, work.Attempt.RequestKey)
        && ValidObject(input.Object, work.Plan.Bucket, InputKey(work.Job.FileId, work.Attempt.RequestKey), MaximumStagingBytes);

    private static MediaAudioProfileModel EffectiveProfile(MediaAudioSettings target, int channels) =>
        new(target.Account, target.Region, target.QueueArn, target.JobRoleArn, SampleRate, channels,
            channels == 1 ? 96_000 : 128_000);

    private static TimeSpan ReconciliationDelay(TimeSpan age) => age.TotalHours >= 1 ? TimeSpan.FromMinutes(5)
        : age.TotalMinutes >= 5 ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(15);

    private static bool CanSubmit(DateTimeOffset now, DateTimeOffset leaseUntil, DateTimeOffset startedAt) =>
        now >= startedAt && now + ProviderBudget < leaseUntil && now + ProviderBudget <= startedAt + SubmitWindow;

    private static bool MatchesProvider(MediaProviderJobModel job, Guid requestKey, string queueArn, string? jobId) =>
        ValidJobId(job.Id) && job.RequestKey == requestKey && job.QueueArn == queueArn && (jobId is null || job.Id == jobId)
        && job.State is "submitted" or "progressing" or "complete" or "error" or "canceled";

    private static bool ValidJobId(string id) => id.Length is > 0 and <= 128
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool ValidObject(MediaStoredVariantModel value, string bucket, string key, int maximumBytes) =>
        value.Bucket == bucket && value.Key == key && IsVersioned(value.VersionId) && value.LastModifiedAt != default
        && value.Size > 0 && value.Size <= maximumBytes && value.Sha256.Length == 32;

    private static string InputKey(long fileId, Guid requestKey) =>
        FormattableString.Invariant($"submissions/transcode-input/{fileId}/{requestKey:N}/input");

    private static string OutputPrefix(long fileId, Guid requestKey) =>
        FormattableString.Invariant($"submissions/transcode-output/{fileId}/{requestKey:N}/");

    private static byte[] ProviderHash(MediaAudioSettings scope, string jobId) => Hash($"{scope.Account}:{scope.Region}:{jobId}");

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
