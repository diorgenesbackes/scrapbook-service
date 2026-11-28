using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaAudioRepositoryStub : IMediaAudioRepository
{
    public MediaJobEntity? Job { get; set; } = MediaAudioTestFixture.Job();
    public MediaSourceEntity? Source { get; set; } = MediaPreparationTestFixture.Source() with { Kind = "audio" };
    public MediaAudioAttemptEntity? Attempt { get; set; }
    public DateTimeOffset? NextAt { get; private set; }
    public bool RetainsLease { get; set; } = true;
    public List<EventWriteTestTransaction> Transactions { get; } = [];
    public List<(MediaConfirmationModel Confirmation, bool Locked, CancellationToken Token)> SourceReads { get; } = [];
    public List<(string Bucket, string InputKey, string OutputKey, EventWriteTestTransaction Transaction)> Starts { get; } = [];
    public List<(MediaStoredVariantModel Object, string Plan, EventWriteTestTransaction Transaction)> Inputs { get; } = [];
    public List<(DateTimeOffset At, EventWriteTestTransaction Transaction)> Submissions { get; } = [];
    public List<(DateTimeOffset RequiredUntil, CancellationToken Token)> LeaseChecks { get; } = [];
    public List<(string State, string? JobId, byte[]? JobHash, string? Code, DateTimeOffset? FinishedAt,
        EventWriteTestTransaction Transaction)> AttemptWrites
    { get; } = [];
    public List<(string State, string? Code, DateTimeOffset At, EventWriteTestTransaction Transaction)> Releases { get; } = [];
    public List<(MediaAudioWorkModel Work, MediaAudioVariantModel Variant, EventWriteTestTransaction Transaction)> Promotions { get; } = [];
    public List<(string JobState, string FileState, string? VariantState, string? Code, DateTimeOffset? RetryAt,
        EventWriteTestTransaction Transaction)> States
    { get; } = [];
    public List<(MediaFailureEntity Failure, EventWriteTestTransaction Transaction)> Failures { get; } = [];
    public List<(MediaAudioWakeModel Target, string JobId, byte[] JobHash, string ExternalId, byte[] ExternalHash,
        byte[] PayloadHash)> Wakes
    { get; } = [];

    private EventWriteTestTransaction CurrentTransaction => Transactions[^1];

    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = new EventWriteTestTransaction();
        Transactions.Add(transaction);
        return Task.FromResult<IEventTransaction>(transaction);
    }

    public Task<MediaJobEntity?> FindEligibleAudioAsync(DateTimeOffset now, bool historical, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Job is { State: "pending" or "running" or "reconciling" } && (NextAt is null || now >= NextAt)
            ? Job : null);
    }

    public Task<MediaSourceEntity?> FindSourceAsync(MediaConfirmationModel message, bool lockFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceReads.Add((message, lockFile, cancellationToken));
        return Task.FromResult(Source);
    }

    public Task<MediaAudioAttemptEntity?> FindAudioAttemptAsync(long jobId, long attemptId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Job?.Id == jobId && Attempt?.Id == attemptId ? Attempt : null);
    }

    public Task<long> StartAudioAsync(MediaJobEntity job, string profile, string bucket, Guid lease, DateTimeOffset leaseUntil,
        Guid requestKey, string inputKey, string outputPrefix, string outputKey, int number, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Starts.Add((bucket, inputKey, outputKey, CurrentTransaction));
        Attempt = new(700 + number, requestKey, outputPrefix, now, null, null, "claimed");
        Job = job with
        {
            State = "running",
            ActiveAttemptId = Attempt.Id,
            AttemptCount = job.AttemptCount + 1,
            LastAttemptNumber = number,
            Profile = profile
        };
        return Task.FromResult(Attempt.Id);
    }

    public Task ResumeAudioAsync(MediaJobEntity job, Guid lease, DateTimeOffset leaseUntil, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<bool> RetainAudioLeaseAsync(MediaAudioWorkModel work, DateTimeOffset requiredUntil, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeaseChecks.Add((requiredUntil, cancellationToken));
        return Task.FromResult(RetainsLease);
    }

    public Task SetAudioInputAsync(MediaAudioWorkModel work, MediaStoredVariantModel input, string profile, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Inputs.Add((input, profile, CurrentTransaction));
        Job = Job! with { Profile = profile };
        return Task.CompletedTask;
    }

    public Task MarkAudioSubmitStartedAsync(long attemptId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Submissions.Add((now, CurrentTransaction));
        Attempt = Attempt! with { SubmittedAt = now, State = "unknown" };
        return Task.CompletedTask;
    }

    public Task SetAudioAttemptAsync(long attemptId, string state, string? providerJobId, byte[]? providerJobHash,
        string? errorCode, DateTimeOffset? finishedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AttemptWrites.Add((state, providerJobId, providerJobHash, errorCode, finishedAt, CurrentTransaction));
        Attempt = Attempt! with { State = state, ProviderJobId = providerJobId ?? Attempt.ProviderJobId };
        return Task.CompletedTask;
    }

    public Task ReleaseAudioLeaseAsync(MediaAudioWorkModel work, string state, string? errorCode, DateTimeOffset nextAt,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Releases.Add((state, errorCode, nextAt, CurrentTransaction));
        Job = Job! with { State = state };
        NextAt = nextAt;
        return Task.CompletedTask;
    }

    public Task PromoteAudioAsync(MediaAudioWorkModel work, MediaAudioVariantModel variant, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Promotions.Add((work, variant, CurrentTransaction));
        return Task.CompletedTask;
    }

    public Task SetAudioStateAsync(MediaJobEntity job, string jobState, string fileState, string? variantState,
        string? errorCode, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        States.Add((jobState, fileState, variantState, errorCode, retryAt, CurrentTransaction));
        Job = Job! with { State = jobState, ActiveAttemptId = null };
        NextAt = retryAt;
        return Task.CompletedTask;
    }

    public Task InsertFailureAsync(MediaFailureEntity result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Failures.Add((result, CurrentTransaction));
        return Task.CompletedTask;
    }

    public Task<MediaAudioWakeModel?> FindAudioWakeAsync(Guid requestKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Job is { ActiveAttemptId: not null } && Attempt is not null && Attempt.RequestKey == requestKey
            ? new MediaAudioWakeModel(Job.Id, Attempt.Id, Job.Profile, Attempt.ProviderJobId, Job.ConfirmationCorrelationId, Job.FileId, Job.FileKind)
            : null);
    }

    public Task<bool> WakeAudioAsync(MediaAudioWakeModel target, Guid requestKey, string providerJobId, byte[] providerJobHash,
        string externalId, byte[] externalHash, byte[] payloadHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Wakes.Any(wake => wake.ExternalId == externalId))
            return Task.FromResult(false);
        Wakes.Add((target, providerJobId, providerJobHash, externalId, externalHash, payloadHash));
        Attempt = Attempt! with { ProviderJobId = providerJobId };
        NextAt = now;
        return Task.FromResult(true);
    }
}
