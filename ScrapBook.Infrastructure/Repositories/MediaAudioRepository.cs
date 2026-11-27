using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class MediaAudioRepository(MediaPreparationRepository repository, MediaAudioSettings settings) : IMediaAudioRepository
{
    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken) => repository.BeginAsync(cancellationToken);

    public Task<MediaSourceEntity?> FindSourceAsync(MediaConfirmationModel message, bool lockFile,
        CancellationToken cancellationToken) => repository.FindSourceAsync(message, lockFile, cancellationToken);

    public Task InsertFailureAsync(MediaFailureEntity result, CancellationToken cancellationToken) =>
        repository.InsertFailureAsync(result, cancellationToken);

    public Task<MediaJobEntity?> FindEligibleAudioAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.QueryAsync<MediaJobEntity>($"""
            SELECT j.id AS "Id", r.records_events_id AS "EventId", r.id AS "RecordId", f.id AS "FileId",
                j.profile_version AS "ProfileVersion", j.version AS "Version", j.state AS "State",
                j.attempt_count AS "AttemptCount", j.retry_cycle AS "RetryCycle",
                j.confirmation_message_id AS "ConfirmationMessageId", j.confirmation_correlation_id AS "ConfirmationCorrelationId",
                j.confirmed_at AS "ConfirmedAt", a.active_id AS "ActiveAttemptId", COALESCE(a.last_number, 0) AS "LastAttemptNumber",
                j.profile::text AS "Profile", f.kind AS "FileKind"
            FROM scrapbook.processing_jobs AS j
            JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
            JOIN scrapbook.records AS r ON r.id = f.files_records_id
            CROSS JOIN LATERAL (
                SELECT max(id) FILTER (WHERE state IN ('created', 'submitted', 'running', 'unknown')) AS active_id,
                    max(number) AS last_number
                FROM scrapbook.processing_attempts
                WHERE processing_attempts_processing_jobs_id = j.id
            ) AS a
            WHERE j.kind = 'av_transcode' AND (f.kind = 'audio' OR ({settings.VideoEnabled} AND f.kind = 'video'))
                AND f.processing_state IN ('pending', 'processing')
                AND j.state IN ('pending', 'running', 'reconciling') AND j.confirmed_at IS NOT NULL
                AND j.next_attempt_at <= {now} AND (j.lease_until IS NULL OR j.lease_until <= {now})
            ORDER BY j.next_attempt_at, j.id
            LIMIT 1
            FOR UPDATE OF j SKIP LOCKED
            """, cancellationToken);

    public Task<MediaAudioAttemptEntity?> FindAudioAttemptAsync(long jobId, long attemptId, CancellationToken cancellationToken) =>
        repository.QueryAsync<MediaAudioAttemptEntity>($"""
            SELECT id AS "Id", request_key AS "RequestKey", output_prefix AS "OutputPrefix", started_at AS "StartedAt",
                provider_submit_started_at AS "SubmittedAt", provider_job_id AS "ProviderJobId", state AS "State"
            FROM scrapbook.processing_attempts
            WHERE processing_attempts_processing_jobs_id = {jobId} AND id = {attemptId} AND provider = 'mediaconvert'
                AND state IN ('created', 'submitted', 'running', 'unknown')
            FOR UPDATE
            """, cancellationToken);

    public async Task<long> StartAudioAsync(MediaJobEntity job, string profile, string bucket, Guid lease, DateTimeOffset leaseUntil,
        Guid requestKey, string inputKey, string outputPrefix, string outputKey, int number, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string[] objectKeys = [inputKey, outputKey];
        var attemptId = await repository.QueryAsync<long>($"""
            WITH leased AS (
                UPDATE scrapbook.processing_jobs
                SET state = 'running', lease_token = {lease}, lease_until = {leaseUntil}, attempt_count = attempt_count + 1,
                    next_attempt_at = {leaseUntil}, updated_at = {now}, version = version + 1, last_error_code = NULL,
                    profile = CAST({profile} AS jsonb)
                WHERE id = {job.Id} AND version = {job.Version} AND kind = 'av_transcode'
                RETURNING id, processing_jobs_files_id
            ), attempt AS (
                INSERT INTO scrapbook.processing_attempts (
                    processing_attempts_processing_jobs_id, processing_attempts_files_id, number, request_key, provider,
                    output_prefix, state, started_at
                )
                SELECT id, processing_jobs_files_id, {number}, {requestKey}, 'mediaconvert', {outputPrefix}, 'created', {now}
                FROM leased
                RETURNING id, processing_attempts_files_id
            ), planned AS (
                INSERT INTO scrapbook.storage_objects (
                    storage_objects_files_id, storage_objects_processing_attempts_id, kind, state, bucket, object_key
                )
                SELECT attempt.processing_attempts_files_id, attempt.id, 'derived', 'planned', {bucket}, targets.key
                FROM attempt CROSS JOIN unnest({objectKeys}) AS targets(key)
            ), variant AS (
                UPDATE scrapbook.file_variants AS v
                SET state = 'processing', updated_at = {now}, version = version + 1, error_code = NULL
                FROM attempt
                WHERE v.file_variants_files_id = attempt.processing_attempts_files_id AND v.profile_version = {job.ProfileVersion}
                    AND v.kind = 'playback' AND v.state IN ('pending', 'processing')
                RETURNING v.id
            ), file AS (
                UPDATE scrapbook.files AS f
                SET processing_state = 'processing', updated_at = {now}, version = version + 1
                FROM attempt
                WHERE f.id = attempt.processing_attempts_files_id AND f.profile_version = {job.ProfileVersion}
                    AND f.kind = {job.FileKind} AND f.processing_state IN ('pending', 'processing')
                RETURNING f.id
            )
            SELECT attempt.id AS "Value" FROM attempt
            WHERE EXISTS (SELECT 1 FROM file) AND EXISTS (SELECT 1 FROM variant)
            """, cancellationToken);
        return attemptId > 0 ? attemptId : throw new MediaPreparationWriteException();
    }

    public Task ResumeAudioAsync(MediaJobEntity job, Guid lease, DateTimeOffset leaseUntil, DateTimeOffset now,
        CancellationToken cancellationToken) => repository.RequireWriteAsync($"""
            UPDATE scrapbook.processing_jobs
            SET lease_token = {lease}, lease_until = {leaseUntil}, next_attempt_at = {leaseUntil}, updated_at = {now},
                version = version + 1
            WHERE id = {job.Id} AND version = {job.Version} AND kind = 'av_transcode'
            """, cancellationToken);

    public Task<bool> RetainAudioLeaseAsync(MediaAudioWorkModel work, DateTimeOffset requiredUntil,
        CancellationToken cancellationToken) => repository.QueryAsync<bool>($"""
            SELECT TRUE AS "Value"
            FROM scrapbook.processing_jobs AS j
            JOIN scrapbook.processing_attempts AS a ON a.processing_attempts_processing_jobs_id = j.id
                AND a.processing_attempts_files_id = j.processing_jobs_files_id
            JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
            WHERE j.id = {work.Job.Id} AND j.version = {work.Job.Version} AND j.kind = 'av_transcode'
                AND j.state IN ('running', 'reconciling') AND j.lease_token = {work.Lease}
                AND j.lease_until > {requiredUntil} AND j.lease_until > clock_timestamp()
                AND f.id = {work.Job.FileId} AND f.kind = {work.Job.FileKind} AND f.processing_state = 'processing'
                AND f.profile_version = {work.Job.ProfileVersion}
                AND a.id = {work.Attempt.Id} AND a.request_key = {work.Attempt.RequestKey}
                AND a.output_prefix = {work.Attempt.OutputPrefix} AND a.provider = 'mediaconvert'
                AND a.state IN ('created', 'submitted', 'running', 'unknown')
                AND (CAST({work.Attempt.ProviderJobId} AS text) IS NULL OR a.provider_job_id = {work.Attempt.ProviderJobId})
            FOR UPDATE OF j, a, f
            """, cancellationToken);

    public Task SetAudioInputAsync(MediaAudioWorkModel work, MediaStoredVariantModel input, string profile, DateTimeOffset now,
        CancellationToken cancellationToken) => repository.RequireWriteAsync($"""
            WITH verified AS (
                UPDATE scrapbook.storage_objects SET state = 'stored', version_id = {input.VersionId},
                    size = {input.Size}, sha256 = {input.Sha256}, last_modified_at = {input.LastModifiedAt},
                    observed_at = {now}, stored_at = {now}, updated_at = {now}, version = version + 1
                WHERE storage_objects_files_id = {work.Job.FileId} AND storage_objects_processing_attempts_id = {work.Attempt.Id}
                    AND bucket = {input.Bucket} AND object_key = {input.Key} AND kind = 'derived' AND state = 'planned'
                RETURNING id
            )
            UPDATE scrapbook.processing_jobs SET profile = CAST({profile} AS jsonb), updated_at = {now}
            WHERE id = {work.Job.Id} AND version = {work.Job.Version} AND EXISTS (SELECT 1 FROM verified)
            """, cancellationToken);

    public Task MarkAudioSubmitStartedAsync(long attemptId, DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.RequireWriteAsync($"""
            UPDATE scrapbook.processing_attempts
            SET provider_submit_started_at = {now}, state = 'unknown', updated_at = {now}, version = version + 1
            WHERE id = {attemptId} AND provider = 'mediaconvert' AND state = 'created'
                AND provider_job_id IS NULL AND provider_submit_started_at IS NULL
            """, cancellationToken);

    public Task SetAudioAttemptAsync(long attemptId, string state, string? providerJobId, byte[]? providerJobHash,
        string? errorCode, DateTimeOffset? finishedAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.RequireWriteAsync($"""
            UPDATE scrapbook.processing_attempts
            SET state = {state}, provider_job_id = COALESCE({providerJobId}, provider_job_id),
                provider_job_hash = COALESCE({providerJobHash}, provider_job_hash), error_code = {errorCode},
                finished_at = {finishedAt}, updated_at = {now}, version = version + 1
            WHERE id = {attemptId} AND provider = 'mediaconvert' AND state IN ('created', 'submitted', 'running', 'unknown')
                AND (CAST({providerJobId} AS text) IS NULL OR provider_job_id IS NULL OR provider_job_id = {providerJobId})
                AND (CAST({finishedAt} AS timestamptz) IS NULL OR provider_job_id IS NOT DISTINCT FROM {providerJobId})
            """, cancellationToken);

    public Task ReleaseAudioLeaseAsync(MediaAudioWorkModel work, string state, string? errorCode, DateTimeOffset nextAt,
        DateTimeOffset now, CancellationToken cancellationToken) => repository.RequireWriteAsync($"""
            UPDATE scrapbook.processing_jobs
            SET state = {state}, last_error_code = {errorCode},
                next_attempt_at = CASE WHEN next_attempt_at < lease_until THEN LEAST(next_attempt_at, {nextAt}) ELSE {nextAt} END,
                lease_token = NULL, lease_until = NULL, updated_at = {now}, version = version + 1
            WHERE id = {work.Job.Id} AND version = {work.Job.Version} AND lease_token = {work.Lease}
            """, cancellationToken);

    public Task PromoteAudioAsync(MediaAudioWorkModel work, MediaAudioVariantModel variant, DateTimeOffset now,
        CancellationToken cancellationToken) => repository.RequireWriteAsync($"""
            WITH verified AS (
                UPDATE scrapbook.storage_objects SET state = 'stored', version_id = {variant.Object.VersionId},
                    size = {variant.Object.Size}, sha256 = {variant.Object.Sha256}, last_modified_at = {variant.Object.LastModifiedAt},
                    observed_at = {now}, stored_at = {now}, updated_at = {now}, version = version + 1
                WHERE storage_objects_files_id = {work.Job.FileId} AND storage_objects_processing_attempts_id = {work.Attempt.Id}
                    AND bucket = {variant.Object.Bucket} AND object_key = {variant.Object.Key}
                    AND kind = 'derived' AND state = 'planned'
                RETURNING id
            )
            UPDATE scrapbook.file_variants AS v
            SET state = 'ready', file_variants_storage_objects_id = verified.id, media_type = {variant.MediaType},
                container = {variant.Container}, audio_codec = {variant.AudioCodec}, duration = {variant.Duration},
                video_codec = {variant.Video?.VideoCodec}, width = {variant.Video?.Width}, height = {variant.Video?.Height},
                frame_rate_numerator = {variant.Video?.FrameRateNumerator},
                frame_rate_denominator = {variant.Video?.FrameRateDenominator},
                location_removed = true, ready_at = {now}, updated_at = {now}, version = version + 1, error_code = NULL
            FROM verified
            WHERE v.file_variants_files_id = {work.Job.FileId} AND v.profile_version = {work.Job.ProfileVersion}
                AND v.kind = 'playback' AND v.state = 'processing'
            """, cancellationToken);

    public Task SetAudioStateAsync(MediaJobEntity job, string jobState, string fileState, string? variantState,
        string? errorCode, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.SetJobStateAsync(job, jobState, fileState, variantState, errorCode, retryAt, now, cancellationToken);

    public Task<MediaAudioWakeModel?> FindAudioWakeAsync(Guid requestKey, CancellationToken cancellationToken) =>
        repository.QueryAsync<MediaAudioWakeModel>($"""
            SELECT j.id AS "JobId", a.id AS "AttemptId", j.profile::text AS "Profile", a.provider_job_id AS "ProviderJobId",
                j.confirmation_correlation_id AS "CorrelationId", f.id AS "FileId", f.kind AS "FileKind"
            FROM scrapbook.processing_attempts AS a
            JOIN scrapbook.processing_jobs AS j ON j.id = a.processing_attempts_processing_jobs_id
            JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
            WHERE a.request_key = {requestKey} AND a.provider = 'mediaconvert' AND a.provider_submit_started_at IS NOT NULL
                AND a.state IN ('submitted', 'running', 'unknown')
                AND j.kind = 'av_transcode' AND j.state IN ('running', 'reconciling')
                AND (f.kind = 'audio' OR ({settings.VideoEnabled} AND f.kind = 'video'))
            """, cancellationToken);

    public Task<bool> WakeAudioAsync(MediaAudioWakeModel target, Guid requestKey, string providerJobId, byte[] providerJobHash,
        string externalId, byte[] externalHash, byte[] payloadHash, DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.QueryAsync<bool>($"""
            WITH eligible AS MATERIALIZED (
                SELECT j.id
                FROM scrapbook.processing_jobs AS j
                JOIN scrapbook.processing_attempts AS a ON a.processing_attempts_processing_jobs_id = j.id
                JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
                WHERE j.id = {target.JobId} AND j.kind = 'av_transcode' AND j.state IN ('running', 'reconciling')
                    AND f.id = {target.FileId} AND f.kind = {target.FileKind} AND f.processing_state = 'processing'
                    AND a.id = {target.AttemptId} AND a.request_key = {requestKey} AND a.provider = 'mediaconvert'
                    AND a.provider_submit_started_at IS NOT NULL AND a.state IN ('submitted', 'running', 'unknown')
                    AND (a.provider_job_id IS NULL OR a.provider_job_id = {providerJobId})
                FOR UPDATE OF j
            ), delivery AS (
                INSERT INTO scrapbook.processed_messages (
                    consumer, source, external_id, external_hash, payload_hash, correlation_id, state, attempt_count, completed_at
                )
                SELECT 'media-preparation', 'mediaconvert', {externalId}, {externalHash}, {payloadHash},
                    {target.CorrelationId}, 'completed', 1, {now}
                FROM eligible
                ON CONFLICT (consumer, source, external_hash) WHERE external_hash IS NOT NULL DO NOTHING
                RETURNING id
            ), identified AS (
                UPDATE scrapbook.processing_attempts
                SET provider_job_id = {providerJobId}, provider_job_hash = {providerJobHash}, updated_at = {now}, version = version + 1
                WHERE id = {target.AttemptId} AND EXISTS (SELECT 1 FROM delivery)
                    AND state IN ('submitted', 'running', 'unknown')
                    AND (provider_job_id IS NULL OR provider_job_id = {providerJobId})
                RETURNING processing_attempts_processing_jobs_id
            )
            UPDATE scrapbook.processing_jobs AS j SET next_attempt_at = LEAST(j.next_attempt_at, {now})
            FROM identified
            WHERE j.id = identified.processing_attempts_processing_jobs_id
            RETURNING TRUE AS "Value"
            """, cancellationToken);
}
