using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class MediaPreparationRepository(ScrapBookDbContext context) : IMediaAdmissionRepository, IMediaPreparationRepository
{
    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken) =>
        ExecuteAsync<IEventTransaction>(async () => new EventTransaction(await context.Database.BeginTransactionAsync(cancellationToken)));

    public Task<MediaSourceEntity?> FindSourceAsync(MediaConfirmationModel message, bool lockFile, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.records_events_id AS "EventId", r.id AS "RecordId", f.id AS "FileId", f.kind AS "Kind",
                r.state AS "RecordState", f.validation_state AS "ValidationState", f.processing_state AS "ProcessingState",
                f.profile_version AS "ProfileVersion", r.confirmed_at AS "RecordConfirmedAt", f.confirmed_at AS "FileConfirmedAt",
                o.bucket AS "Bucket", o.object_key AS "ObjectKey", o.version_id AS "VersionId", o.size AS "Size",
                f.sha256 AS "Sha256", o.sha256 AS "OriginalSha256"
            FROM scrapbook.files AS f
            JOIN scrapbook.records AS r ON r.id = f.files_records_id
            JOIN scrapbook.storage_objects AS o ON o.storage_objects_files_id = f.id
            WHERE r.records_events_id = {0} AND r.id = {1} AND f.id = {2}
                AND o.kind = 'original' AND o.state = 'stored' AND o.size = f.actual_size
                AND f.sha256 IS NOT NULL
            """;
        return QueryAsync<MediaSourceEntity>(FormattableStringFactory.Create(lockFile ? sql + " FOR UPDATE OF f" : sql,
            message.EventId, message.RecordId, message.FileId), cancellationToken);
    }

    public Task<ProcessedMessageEntity?> FindDeliveryAsync(string consumer, string source, Guid messageId,
        CancellationToken cancellationToken) =>
        QueryAsync<ProcessedMessageEntity>($"""
            SELECT id AS "Id", payload_hash AS "PayloadHash", state AS "State", error_code AS "ErrorCode"
            FROM scrapbook.processed_messages
            WHERE consumer = {consumer} AND source = {source} AND message_id = {messageId}
            """, cancellationToken);

    public Task InsertDeliveryAsync(string consumer, string source, MediaConfirmationModel message, byte[] hash,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scrapbook.processed_messages (
                consumer, source, message_id, payload_hash, correlation_id, state, attempt_count, completed_at
            )
            VALUES ({consumer}, {source}, {message.MessageId}, {hash}, {message.CorrelationId}, 'completed', 1, {now})
            """, cancellationToken));

    public async Task<bool> InsertJobAsync(MediaConfirmationModel message, int profileVersion, string kind, string profile,
        string[] variantKinds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH job AS (
                INSERT INTO scrapbook.processing_jobs (
                    processing_jobs_files_id, kind, profile_version, profile, state, next_attempt_at,
                    confirmation_message_id, confirmation_correlation_id, confirmed_at
                )
                VALUES (
                    {message.FileId}, {kind}, {profileVersion}, CAST({profile} AS jsonb), 'pending', {now},
                    {message.MessageId}, {message.CorrelationId}, {message.ConfirmedAt}
                )
                ON CONFLICT (processing_jobs_files_id, kind, profile_version) DO NOTHING
                RETURNING processing_jobs_files_id
            )
            INSERT INTO scrapbook.file_variants (file_variants_files_id, kind, profile_version, state)
            SELECT job.processing_jobs_files_id, variant.kind, {profileVersion}, 'pending'
            FROM job CROSS JOIN unnest({variantKinds}) AS variant(kind)
            """, cancellationToken));
        return rows == variantKinds.Length;
    }

    public Task<MediaJobEntity?> FindEligiblePhotoAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        QueryAsync<MediaJobEntity>($"""
            SELECT j.id AS "Id", r.records_events_id AS "EventId", r.id AS "RecordId", f.id AS "FileId",
                j.profile_version AS "ProfileVersion", j.version AS "Version", j.state AS "State",
                j.attempt_count AS "AttemptCount", j.retry_cycle AS "RetryCycle",
                j.confirmation_message_id AS "ConfirmationMessageId", j.confirmation_correlation_id AS "ConfirmationCorrelationId",
                j.confirmed_at AS "ConfirmedAt", a.active_id AS "ActiveAttemptId", COALESCE(a.last_number, 0) AS "LastAttemptNumber",
                j.profile::text AS "Profile"
            FROM scrapbook.processing_jobs AS j
            JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
            JOIN scrapbook.records AS r ON r.id = f.files_records_id
            CROSS JOIN LATERAL (
                SELECT max(id) FILTER (WHERE state IN ('created', 'submitted', 'running', 'unknown')) AS active_id,
                    max(number) AS last_number
                FROM scrapbook.processing_attempts
                WHERE processing_attempts_processing_jobs_id = j.id
            ) AS a
            WHERE j.kind = 'photo_prepare' AND j.state IN ('pending', 'running') AND j.confirmed_at IS NOT NULL
                AND j.next_attempt_at <= {now} AND (j.lease_until IS NULL OR j.lease_until <= {now})
            ORDER BY j.next_attempt_at, j.id
            LIMIT 1
            FOR UPDATE OF j SKIP LOCKED
            """, cancellationToken);

    public async Task<long> StartPhotoAsync(MediaJobEntity job, string bucket, Guid lease, DateTimeOffset leaseUntil,
        Guid requestKey, string outputPrefix, int number, string[] variantKinds, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var attemptId = await QueryAsync<long>($"""
            WITH leased AS (
                UPDATE scrapbook.processing_jobs
                SET state = 'running', lease_token = {lease}, lease_until = {leaseUntil}, attempt_count = attempt_count + 1,
                    next_attempt_at = {leaseUntil}, updated_at = {now}, version = version + 1, last_error_code = NULL
                WHERE id = {job.Id} AND version = {job.Version}
                RETURNING id, processing_jobs_files_id
            ), attempt AS (
                INSERT INTO scrapbook.processing_attempts (
                    processing_attempts_processing_jobs_id, processing_attempts_files_id, number, request_key, provider,
                    output_prefix, state, started_at
                )
                SELECT id, processing_jobs_files_id, {number}, {requestKey}, 'local', {outputPrefix}, 'running', {now}
                FROM leased
                RETURNING id, processing_attempts_files_id
            ), planned AS (
                INSERT INTO scrapbook.storage_objects (
                    storage_objects_files_id, storage_objects_processing_attempts_id, kind, state, bucket, object_key
                )
                SELECT attempt.processing_attempts_files_id, attempt.id, 'derived', 'planned', {bucket},
                    {outputPrefix} || variant.kind || '.jpg'
                FROM attempt CROSS JOIN unnest({variantKinds}) AS variant(kind)
            ), variants AS (
                UPDATE scrapbook.file_variants AS v
                SET state = 'processing', updated_at = {now}, version = version + 1
                FROM attempt
                WHERE v.file_variants_files_id = attempt.processing_attempts_files_id AND v.profile_version = {job.ProfileVersion}
                    AND v.kind = ANY({variantKinds}) AND v.state IN ('pending', 'processing')
                RETURNING v.id
            ), file AS (
                UPDATE scrapbook.files AS f
                SET processing_state = 'processing', updated_at = {now}, version = version + 1
                FROM attempt
                WHERE f.id = attempt.processing_attempts_files_id AND f.profile_version = {job.ProfileVersion}
                    AND f.processing_state IN ('pending', 'processing')
                RETURNING f.id
            )
            SELECT attempt.id AS "Value" FROM attempt
            WHERE EXISTS (SELECT 1 FROM file) AND (SELECT count(*) FROM variants) = {variantKinds.Length}
            """, cancellationToken);
        return attemptId > 0 ? attemptId : throw new MediaPreparationWriteException();
    }

    public Task EndAttemptAsync(long attemptId, string state, string? errorCode, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        RequireWriteAsync($"""
            UPDATE scrapbook.processing_attempts SET state = {state}, error_code = {errorCode}, finished_at = {now},
                updated_at = {now}, version = version + 1
            WHERE id = {attemptId} AND state = 'running'
            """, cancellationToken);

    public Task<bool> RetainPhotoLeaseAsync(PhotoPreparationWorkModel work, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        QueryAsync<bool>($"""
            SELECT TRUE AS "Value"
            FROM scrapbook.processing_jobs AS j
            JOIN scrapbook.processing_attempts AS a ON a.processing_attempts_processing_jobs_id = j.id
                AND a.processing_attempts_files_id = j.processing_jobs_files_id
            JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
            WHERE j.id = {work.Job.Id} AND j.version = {work.Job.Version} AND j.state = 'running'
                AND j.lease_token = {work.Lease} AND j.lease_until > {now} AND j.lease_until > clock_timestamp()
                AND f.id = {work.Job.FileId} AND f.processing_state = 'processing' AND f.profile_version = {work.Job.ProfileVersion}
                AND a.id = {work.AttemptId} AND a.state = 'running' AND a.output_prefix = {work.OutputPrefix}
            FOR UPDATE OF j, a, f
            """, cancellationToken);

    public Task PromoteVariantAsync(PhotoPreparationWorkModel work, PreparedPhotoVariantModel variant, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        RequireWriteAsync($"""
            WITH verified AS (
                UPDATE scrapbook.storage_objects SET state = 'stored', version_id = {variant.Storage.VersionId},
                    size = {variant.Storage.Size}, sha256 = {variant.Storage.Sha256},
                    last_modified_at = {variant.Storage.LastModifiedAt}, observed_at = {now}, stored_at = {now},
                    updated_at = {now}, version = version + 1
                WHERE storage_objects_files_id = {work.Job.FileId} AND storage_objects_processing_attempts_id = {work.AttemptId}
                    AND bucket = {variant.Storage.Bucket} AND object_key = {variant.Storage.Key}
                    AND kind = 'derived' AND state = 'planned'
                RETURNING id
            )
            UPDATE scrapbook.file_variants AS v
            SET state = 'ready', file_variants_storage_objects_id = verified.id, media_type = 'image/jpeg', container = 'jpeg',
                width = {variant.Width}, height = {variant.Height}, location_removed = true, ready_at = {now},
                updated_at = {now}, version = version + 1, error_code = NULL
            FROM verified
            WHERE v.file_variants_files_id = {work.Job.FileId} AND v.profile_version = {work.Job.ProfileVersion}
                AND v.kind = {variant.Kind} AND v.state = 'processing'
            """, cancellationToken);

    public Task SetPhotoStateAsync(MediaJobEntity job, string jobState, string fileState, string? variantState, string? errorCode,
        DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        SetJobStateAsync(job, jobState, fileState, variantState, errorCode, retryAt, now, cancellationToken);

    internal Task SetJobStateAsync(MediaJobEntity job, string jobState, string fileState, string? variantState, string? errorCode,
        DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var terminalAt = retryAt is null ? now : (DateTimeOffset?)null;
        return RequireWriteAsync($"""
            WITH changed AS (
                UPDATE scrapbook.processing_jobs
                SET state = {jobState}, last_error_code = {errorCode}, next_attempt_at = COALESCE({retryAt}, {now}),
                    completed_at = {terminalAt}, lease_token = NULL, lease_until = NULL, updated_at = {now}, version = version + 1
                WHERE id = {job.Id} AND version = {job.Version}
                RETURNING processing_jobs_files_id
            ), variants AS (
                UPDATE scrapbook.file_variants AS v
                SET state = {variantState}, error_code = {errorCode}, updated_at = {now}, version = version + 1
                FROM changed
                WHERE v.file_variants_files_id = changed.processing_jobs_files_id AND v.profile_version = {job.ProfileVersion}
                    AND CAST({variantState} AS text) IS NOT NULL AND v.state IN ('pending', 'processing')
            )
            UPDATE scrapbook.files AS f
            SET processing_state = {fileState}, updated_at = {now}, version = version + 1
            FROM changed
            WHERE f.id = changed.processing_jobs_files_id AND f.profile_version = {job.ProfileVersion}
            """, cancellationToken);
    }

    public Task InsertFailureAsync(MediaFailureEntity result, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scrapbook.outbox_messages (
                message_id, type, protocol_version, aggregate_type, aggregate_id, payload,
                correlation_id, causation_id, occurred_at, state, available_at
            )
            VALUES (
                {result.MessageId}, {result.Type}, 1, 'event', {result.EventId}, CAST({result.Payload} AS jsonb),
                {result.CorrelationId}, {result.CausationId}, {result.OccurredAt}, 'pending', {result.OccurredAt}
            )
            ON CONFLICT (message_id) DO NOTHING
            """, cancellationToken));

    public async Task<MediaFileProjectionModel?> GetFileAsync(long eventId, long recordId, long fileId,
        CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.SqlQuery<MediaFileProjectionEntity>($"""
            SELECT f.id AS "FileId", f.processing_state AS "State", f.profile_version AS "ProfileVersion",
                COALESCE(ready.variants, '[]'::jsonb)::text AS "Variants"
            FROM scrapbook.files AS f
            JOIN scrapbook.records AS r ON r.id = f.files_records_id
            LEFT JOIN LATERAL (
                SELECT jsonb_agg(jsonb_build_object(
                    'Kind', v.kind, 'StorageObjectId', o.id, 'VersionId', o.version_id, 'MediaType', v.media_type
                ) ORDER BY v.kind) AS variants
                FROM scrapbook.file_variants AS v
                JOIN scrapbook.storage_objects AS o ON o.id = v.file_variants_storage_objects_id
                    AND o.storage_objects_files_id = v.file_variants_files_id AND o.kind = 'derived' AND o.state = 'stored'
                JOIN scrapbook.processing_attempts AS a ON a.id = o.storage_objects_processing_attempts_id
                    AND a.state = 'succeeded'
                JOIN scrapbook.processing_jobs AS j ON j.id = a.processing_attempts_processing_jobs_id
                    AND j.profile_version = v.profile_version AND j.state = 'completed'
                WHERE f.processing_state = 'available' AND v.file_variants_files_id = f.id
                    AND v.profile_version = f.profile_version AND v.state = 'ready'
            ) AS ready ON TRUE
            WHERE r.records_events_id = {eventId} AND r.id = {recordId} AND f.id = {fileId}
                AND r.confirmed_at IS NOT NULL AND f.validation_state = 'valid' AND f.confirmed_at IS NOT NULL
            """).ToListAsync(cancellationToken));
        var row = rows.SingleOrDefault();
        return row is null ? null : new(row.FileId, row.State, row.ProfileVersion,
            JsonSerializer.Deserialize<MediaVariantReferenceModel[]>(row.Variants) ?? []);
    }

    internal Task<T?> QueryAsync<T>(FormattableString sql, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => (await context.Database.SqlQuery<T>(sql).ToListAsync(cancellationToken)).SingleOrDefault());

    internal async Task RequireWriteAsync(FormattableString sql, CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync(sql, cancellationToken));
        if (rows != 1) throw new MediaPreparationWriteException();
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
        catch (PostgresException error) when (error.SqlState.StartsWith("23", StringComparison.Ordinal)
            || error.SqlState == PostgresErrorCodes.RaiseException)
        {
            throw new PostgresWriteRejectedException(error);
        }
    }
}
