using System.Runtime.CompilerServices;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class MediaPreparationRecoveryRepository(MediaPreparationRepository repository) : IMediaPreparationRecoveryRepository
{
    public Task<OutboxMessageEntity?> FindFactAsync(long eventId, Guid messageId, CancellationToken cancellationToken) =>
        repository.QueryAsync<OutboxMessageEntity>($"""
            SELECT message_id AS "MessageId", type AS "Type", protocol_version AS "ProtocolVersion",
                aggregate_type AS "AggregateType", aggregate_id AS "EventId", payload::text AS "Payload",
                correlation_id AS "CorrelationId", occurred_at AS "OccurredAt", attempt_count AS "AttemptCount",
                lease_token AS "LeaseToken", state AS "State"
            FROM scrapbook.outbox_messages
            WHERE aggregate_type = 'event' AND aggregate_id = {eventId} AND message_id = {messageId}
            """, cancellationToken);

    public Task<ProcessedMessageEntity?> FindDeliveryAsync(long eventId, Guid messageId, string consumer, string source,
        CancellationToken cancellationToken) =>
        repository.QueryAsync<ProcessedMessageEntity>($"""
            SELECT delivery.id AS "Id", delivery.payload_hash AS "PayloadHash", delivery.state AS "State",
                delivery.error_code AS "ErrorCode"
            FROM scrapbook.processed_messages AS delivery
            JOIN scrapbook.outbox_messages AS fact ON fact.message_id = delivery.message_id
            WHERE fact.aggregate_type = 'event' AND fact.aggregate_id = {eventId} AND fact.message_id = {messageId}
                AND delivery.consumer = {consumer} AND delivery.source = {source}
            """, cancellationToken);

    public Task<MediaRecoveryJobEntity?> FindJobAsync(long eventId, long fileId, int profileVersion, bool lockJob,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT j.id AS "Id", r.id AS "RecordId", f.id AS "FileId", j.version AS "Version", j.retry_cycle AS "RetryCycle",
                j.state AS "State", f.kind AS "FileKind", j.confirmation_message_id AS "ConfirmationMessageId",
                j.confirmation_correlation_id AS "ConfirmationCorrelationId", j.confirmed_at AS "ConfirmedAt",
                j.lease_token IS NOT NULL AS "HasLease",
                EXISTS (SELECT 1 FROM scrapbook.processing_attempts AS a
                    WHERE a.processing_attempts_processing_jobs_id = j.id AND a.processing_attempts_files_id = f.id
                        AND a.state IN ('created', 'submitted', 'running', 'unknown')) AS "HasActiveAttempt",
                COALESCE((SELECT max(a.number) FROM scrapbook.processing_attempts AS a
                    WHERE a.processing_attempts_processing_jobs_id = j.id AND a.processing_attempts_files_id = f.id), 0)
                    AS "LastAttemptNumber"
            FROM scrapbook.processing_jobs AS j
            JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
            JOIN scrapbook.records AS r ON r.id = f.files_records_id
            WHERE r.records_events_id = {0} AND f.id = {1} AND j.profile_version = {2}
                AND j.kind = CASE WHEN f.kind = 'photo' THEN 'photo_prepare' ELSE 'av_transcode' END
            """;
        return repository.QueryAsync<MediaRecoveryJobEntity>(
            FormattableStringFactory.Create(lockJob ? sql + " FOR UPDATE OF j" : sql, eventId, fileId, profileVersion),
            cancellationToken);
    }

    public Task<string[]?> FindFailedVariantsAsync(long eventId, long fileId, int profileVersion,
        CancellationToken cancellationToken) =>
        repository.QueryAsync<string[]>($"""
            SELECT ARRAY(
                SELECT v.kind::text
                FROM scrapbook.file_variants AS v
                JOIN scrapbook.files AS f ON f.id = v.file_variants_files_id AND f.profile_version = v.profile_version
                JOIN scrapbook.records AS r ON r.id = f.files_records_id
                WHERE r.records_events_id = {eventId} AND f.id = {fileId} AND v.profile_version = {profileVersion}
                    AND v.state = 'failed' AND v.file_variants_storage_objects_id IS NULL
                ORDER BY v.kind
            ) AS "Value"
            """, cancellationToken);

    public Task ReopenAsync(long eventId, MediaRecoveryJobEntity job, int profileVersion, int nextCycle, string[] variantKinds,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.RequireWriteAsync($"""
            WITH job AS (
                UPDATE scrapbook.processing_jobs AS j
                SET state = 'pending', attempt_count = 0, retry_cycle = {nextCycle}, next_attempt_at = {now},
                    completed_at = NULL, last_error_code = NULL, updated_at = {now}, version = j.version + 1
                FROM scrapbook.files AS f
                JOIN scrapbook.records AS r ON r.id = f.files_records_id
                WHERE r.records_events_id = {eventId} AND f.id = {job.FileId} AND f.profile_version = {profileVersion}
                    AND j.id = {job.Id} AND j.processing_jobs_files_id = f.id AND j.profile_version = f.profile_version
                    AND j.version = {job.Version} AND j.retry_cycle = {job.RetryCycle} AND j.state = 'failed'
                    AND j.lease_token IS NULL
                    AND NOT EXISTS (SELECT 1 FROM scrapbook.processing_attempts AS a
                        WHERE a.processing_attempts_processing_jobs_id = j.id AND a.processing_attempts_files_id = f.id
                            AND a.state IN ('created', 'submitted', 'running', 'unknown'))
                RETURNING j.processing_jobs_files_id, j.profile_version
            ), variants AS (
                UPDATE scrapbook.file_variants AS v
                SET state = 'pending', error_code = NULL, updated_at = {now}, version = version + 1
                FROM job
                WHERE v.file_variants_files_id = job.processing_jobs_files_id AND v.profile_version = job.profile_version
                    AND v.kind = ANY({variantKinds}) AND v.state = 'failed'
                    AND v.file_variants_storage_objects_id IS NULL
                RETURNING v.id
            )
            UPDATE scrapbook.files AS f
            SET processing_state = 'pending', updated_at = {now}, version = version + 1
            FROM job
            WHERE f.id = job.processing_jobs_files_id AND f.profile_version = job.profile_version
                AND f.processing_state = 'failed' AND (SELECT count(*) FROM variants) = {variantKinds.Length}
            """, cancellationToken);
}
