using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaAudioRepository
{
    Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken);
    Task<MediaJobEntity?> FindEligibleAudioAsync(DateTimeOffset now, bool historical, bool reconciliationOnly,
        CancellationToken cancellationToken);
    Task<MediaSourceEntity?> FindSourceAsync(MediaConfirmationModel message, bool lockFile, CancellationToken cancellationToken);
    Task<MediaAudioAttemptEntity?> FindAudioAttemptAsync(long jobId, long attemptId, CancellationToken cancellationToken);
    Task<long> StartAudioAsync(MediaJobEntity job, string profile, string bucket, Guid lease, DateTimeOffset leaseUntil,
        Guid requestKey, string inputKey, string outputPrefix, string outputKey, int number, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task ResumeAudioAsync(MediaJobEntity job, Guid lease, DateTimeOffset leaseUntil, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<bool> RetainAudioLeaseAsync(MediaAudioWorkModel work, DateTimeOffset requiredUntil, CancellationToken cancellationToken);
    Task SetAudioInputAsync(MediaAudioWorkModel work, MediaStoredVariantModel input, string profile, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task MarkAudioSubmitStartedAsync(long attemptId, DateTimeOffset now, CancellationToken cancellationToken);
    Task SetAudioAttemptAsync(long attemptId, string state, string? providerJobId, byte[]? providerJobHash,
        string? errorCode, DateTimeOffset? finishedAt, DateTimeOffset now, CancellationToken cancellationToken);
    Task ReleaseAudioLeaseAsync(MediaAudioWorkModel work, string state, string? errorCode, DateTimeOffset nextAt,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task PromoteAudioAsync(MediaAudioWorkModel work, MediaAudioVariantModel variant, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task SetAudioStateAsync(MediaJobEntity job, string jobState, string fileState, string? variantState,
        string? errorCode, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken);
    Task InsertFailureAsync(MediaFailureEntity result, CancellationToken cancellationToken);
    Task<MediaAudioWakeModel?> FindAudioWakeAsync(Guid requestKey, CancellationToken cancellationToken);
    Task<bool> WakeAudioAsync(MediaAudioWakeModel target, Guid requestKey, string providerJobId, byte[] providerJobHash,
        string externalId, byte[] externalHash, byte[] payloadHash, DateTimeOffset now, CancellationToken cancellationToken);
}
