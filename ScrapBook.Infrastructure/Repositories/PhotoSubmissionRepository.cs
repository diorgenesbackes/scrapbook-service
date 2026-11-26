using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class PhotoSubmissionRepository(ScrapBookDbContext context) : IPhotoSubmissionRepository
{
    private const string SelectSubmission = """
        SELECT session.id AS "SessionId", record.records_events_id AS "EventId", record.id AS "RecordId",
            file.id AS "FileId", temporary.id AS "TemporaryObjectId", original.id AS "OriginalObjectId",
            session.state AS "State", temporary.bucket AS "Bucket",
            temporary.object_key AS "TemporaryKey", original.object_key AS "OriginalKey",
            temporary.version_id AS "TemporaryVersion", temporary.receipt_event_at AS "ReceiptAt",
            file.client_key AS "FileKey", file.declared_type AS "DeclaredMediaType",
            file.declared_size AS "DeclaredSize", file.sha256 AS "Sha256",
            session.started_at AS "StartedAt", session.completion_deadline_at AS "CompletionDeadlineAt",
            session.terminal_at AS "TerminalAt", session.request_correlation_id AS "CorrelationId"
        FROM scrapbook.upload_sessions AS session
        JOIN scrapbook.records AS record ON record.id = session.upload_sessions_records_id
        JOIN scrapbook.files AS file ON file.files_records_id = record.id
        JOIN scrapbook.storage_objects AS temporary
            ON temporary.storage_objects_files_id = file.id AND temporary.kind = 'temporary'
        JOIN scrapbook.storage_objects AS original
            ON original.storage_objects_files_id = file.id AND original.kind = 'original'
        """;

    public async Task<EventEntity?> LockEventAsync(long eventId, CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Set<EventEntity>().FromSqlInterpolated(
            $"SELECT event.* FROM scrapbook.events AS event WHERE event.id = {eventId} FOR UPDATE OF event")
            .AsNoTracking().ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public async Task InsertAsync(CreatePhotoSubmissionCommand command, CancellationToken cancellationToken)
    {
        await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH inserted_record AS (
                INSERT INTO scrapbook.records (
                    records_events_id, records_consent_notices_id, category, format, sender_name, caption,
                    expected_file_count, consented_at, submitted_at, state
                ) VALUES (
                    {command.Event.Id}, {command.NoticeId}, 'testimonial', 'photo', {command.GuestName}, {command.Caption},
                    1, {Civil("consented_at", command.ConsentedAt)}, {Civil("submitted_at", command.SubmittedAt)}, 'receiving'
                ) RETURNING id
            ), inserted_operation AS (
                INSERT INTO scrapbook.idempotent_operations (
                    idempotent_operations_events_id, idempotent_operations_records_id, operation, key, payload_hash,
                    authorization_hash, hash_key_id, authorization_expires_at, state
                )
                SELECT {command.Event.Id}, id, {command.Operation}, {command.AttemptKey}, {command.PayloadHash},
                    {command.AuthorizationHash}, {command.HashKeyId}, {command.AuthorizationExpiresAt}, 'in_progress'
                FROM inserted_record
                RETURNING id, idempotent_operations_records_id
            ), inserted_session AS (
                INSERT INTO scrapbook.upload_sessions (
                    upload_sessions_events_id, upload_sessions_records_id, upload_sessions_idempotent_operations_id,
                    state, window_opens_at, window_closes_at, completion_deadline_at, started_at, request_correlation_id
                )
                SELECT {command.Event.Id}, idempotent_operations_records_id, id, 'receiving',
                    {Civil("window_opens_at", command.Event.SubmissionOpensAt)}, {Civil("window_closes_at", command.Event.SubmissionClosesAt)},
                    {Civil("completion_deadline_at", command.Event.CompletionDeadlineAt)}, {Civil("started_at", command.SubmittedAt)},
                    {command.CorrelationId}
                FROM inserted_operation
                RETURNING upload_sessions_records_id
            ), inserted_file AS (
                INSERT INTO scrapbook.files (
                    files_records_id, position, client_key, kind, declared_type, declared_size, sha256,
                    validation_state, processing_state
                )
                SELECT upload_sessions_records_id, 1, {command.File.FileKey}, 'photo', {command.File.DeclaredMediaType},
                    {command.File.SizeBytes}, {command.File.Sha256}, 'expected', 'not_requested'
                FROM inserted_session RETURNING id
            )
            INSERT INTO scrapbook.storage_objects (storage_objects_files_id, kind, state, bucket, object_key)
            SELECT file.id, object.kind, 'planned', {command.Bucket}, object.key
            FROM inserted_file AS file
            CROSS JOIN (VALUES ('temporary', {command.TemporaryKey}), ('original', {command.OriginalKey})) AS object(kind, key)
            """, cancellationToken));
    }

    public Task<PhotoSubmissionEntity?> FindForRecordAsync(long recordId, CancellationToken cancellationToken) =>
        FindAsync(SelectSubmission + " WHERE record.id = {0}", [recordId], cancellationToken);

    public Task<PhotoSubmissionEntity?> FindForObjectAsync(string bucket, string key, CancellationToken cancellationToken) =>
        FindAsync(SelectSubmission + " WHERE temporary.bucket = {0} AND temporary.object_key = {1} FOR UPDATE OF session",
            [bucket, key], cancellationToken);

    public Task<PhotoSubmissionEntity?> FindWorkAsync(DateTimeOffset now, DateTime expiryBefore, CancellationToken cancellationToken) =>
        FindAsync(SelectSubmission + """
             WHERE session.next_attempt_at <= {0} AND (session.lease_until IS NULL OR session.lease_until <= {0})
                AND (session.state = 'validating' OR (session.state = 'receiving' AND session.completion_deadline_at <= {1}))
             ORDER BY session.next_attempt_at, session.id LIMIT 1
            """, [now, Civil("expiry_before", expiryBefore)], cancellationToken);

    public async Task<bool> TryLeaseAsync(long sessionId, string state, Guid lease, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        var count = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.upload_sessions
            SET lease_token = {lease}, lease_until = {leaseUntil}, attempt_count = attempt_count + 1
            WHERE id = {sessionId} AND state = {state} AND next_attempt_at <= {now}
                AND (lease_until IS NULL OR lease_until <= {now})
            """, cancellationToken));
        return count == 1;
    }

    public Task ScheduleAsync(long sessionId, Guid lease, DateTimeOffset dueAt, string? error, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.upload_sessions
            SET lease_token = NULL, lease_until = NULL, next_attempt_at = {dueAt}, last_error_code = {error}
            WHERE id = {sessionId} AND lease_token = {lease}
            """, cancellationToken));

    public Task StoreReceiptAsync(long storageObjectId, PhotoReceiptModel receipt, DateTimeOffset cleanupAfter,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scrapbook.photo_upload_receipts (storage_object_id, version_id, received_at, size, cleanup_after)
            VALUES ({storageObjectId}, {receipt.VersionId}, {receipt.ReceivedAt}, {receipt.Size}, {cleanupAfter})
            ON CONFLICT (storage_object_id, version_id) DO NOTHING
            """, cancellationToken));

    public Task PinAsync(PhotoSubmissionEntity submission, PhotoReceiptModel receipt, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH updated AS (
                UPDATE scrapbook.upload_sessions
                SET state = 'validating', next_attempt_at = {now}, sealed_at = {now}, lease_token = NULL, lease_until = NULL
                WHERE id = {submission.SessionId} AND state = 'receiving'
                RETURNING id
            ), updated_record AS (
                UPDATE scrapbook.records SET state = 'validating'
                WHERE id = {submission.RecordId} AND EXISTS (SELECT 1 FROM updated)
            ), updated_file AS (
                UPDATE scrapbook.files SET validation_state = 'validating'
                WHERE id = {submission.FileId} AND EXISTS (SELECT 1 FROM updated)
            )
            UPDATE scrapbook.storage_objects
            SET state = 'uploading', version_id = {receipt.VersionId}, receipt_event_at = {receipt.ReceivedAt},
                receipt_event_kind = 'put', observed_at = {now}
            WHERE id = {submission.TemporaryObjectId} AND EXISTS (SELECT 1 FROM updated)
            """, cancellationToken));

    public async Task<bool> HasReceiptAsync(long storageObjectId, string versionId, CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM scrapbook.photo_upload_receipts
                WHERE storage_object_id = {storageObjectId} AND version_id = {versionId}
            ) AS "Value"
            """).ToListAsync(cancellationToken));
        return rows.Single();
    }

    public async Task<bool> ConfirmAsync(ConfirmPhotoSubmissionCommand command, CancellationToken cancellationToken)
    {
        var submission = command.Submission;
        var count = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH updated_session AS (
                UPDATE scrapbook.upload_sessions
                SET state = 'confirmed', terminal_at = {command.CommittedAt}, lease_token = NULL, lease_until = NULL,
                    last_error_code = NULL
                WHERE id = {submission.SessionId} AND lease_token = {command.Lease} AND state = 'validating'
                RETURNING upload_sessions_idempotent_operations_id
            ), updated_record AS (
                UPDATE scrapbook.records
                SET state = 'confirmed', received_at = {Civil("record_received", command.ReceivedAt)},
                    confirmed_at = {Civil("record_confirmed", command.ConfirmedAt)}
                WHERE id = {submission.RecordId} AND EXISTS (SELECT 1 FROM updated_session)
            ), updated_file AS (
                UPDATE scrapbook.files
                SET actual_size = {command.Original.Size}, media_type = {command.Image.MediaType}, container = {command.Image.Container},
                    width = {command.Image.Width}, height = {command.Image.Height}, orientation = {command.Image.Orientation},
                    validation_state = 'valid', processing_state = 'pending',
                    received_at = {Civil("file_received", command.ReceivedAt)}, validated_at = {command.CommittedAt},
                    confirmed_at = {Civil("file_confirmed", command.ConfirmedAt)}
                WHERE id = {submission.FileId} AND EXISTS (SELECT 1 FROM updated_session)
            ), updated_object AS (
                UPDATE scrapbook.storage_objects
                SET state = 'stored', version_id = {command.Original.VersionId}, size = {command.Original.Size},
                    sha256 = {submission.Sha256}, last_modified_at = {command.Original.LastModifiedAt},
                    observed_at = {command.CommittedAt}, stored_at = {command.CommittedAt}
                WHERE id = {submission.OriginalObjectId} AND EXISTS (SELECT 1 FROM updated_session)
            )
            UPDATE scrapbook.idempotent_operations
            SET state = 'confirmed', completed_at = {command.CommittedAt}
            WHERE id IN (SELECT upload_sessions_idempotent_operations_id FROM updated_session)
            """, cancellationToken));
        return count == 1;
    }

    public Task TerminateAsync(PhotoSubmissionEntity submission, Guid lease, string state, string code, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH updated_session AS (
                UPDATE scrapbook.upload_sessions
                SET state = {state}, terminal_at = {now}, lease_token = NULL, lease_until = NULL, last_error_code = {code}
                WHERE id = {submission.SessionId} AND lease_token = {lease} AND state = {submission.State}
                RETURNING upload_sessions_idempotent_operations_id
            ), updated_record AS (
                UPDATE scrapbook.records SET state = 'rejected', rejection_code = {code}
                WHERE id = {submission.RecordId} AND EXISTS (SELECT 1 FROM updated_session)
            ), updated_file AS (
                UPDATE scrapbook.files SET validation_state = 'rejected', error_code = {code}
                WHERE id = {submission.FileId} AND EXISTS (SELECT 1 FROM updated_session)
            )
            UPDATE scrapbook.idempotent_operations
            SET state = {state}, completed_at = {now}, result_code = {code}
            WHERE id IN (SELECT upload_sessions_idempotent_operations_id FROM updated_session)
            """, cancellationToken));

    public async Task<PhotoCleanupModel?> FindCleanupAsync(DateTimeOffset now, DateTimeOffset terminalBefore,
        CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.SqlQuery<PhotoCleanupModel>($"""
            SELECT temporary.id AS "StorageObjectId", temporary.bucket AS "Bucket", temporary.object_key AS "Key",
                receipt.version_id AS "VersionId"
            FROM scrapbook.photo_upload_receipts AS receipt
            JOIN scrapbook.storage_objects AS temporary ON temporary.id = receipt.storage_object_id AND temporary.kind = 'temporary'
            JOIN scrapbook.files AS file ON file.id = temporary.storage_objects_files_id
            JOIN scrapbook.upload_sessions AS session ON session.upload_sessions_records_id = file.files_records_id
            WHERE receipt.deleted_at IS NULL AND receipt.cleanup_after <= {now}
                AND ((session.state = 'expired'
                    AND session.completion_deadline_at <= ({terminalBefore} AT TIME ZONE 'America/Sao_Paulo'))
                    OR (session.state IN ('confirmed', 'rejected') AND session.terminal_at <= {terminalBefore}))
            ORDER BY receipt.cleanup_after, receipt.id LIMIT 1
            """).ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public Task MarkDeletedAsync(PhotoCleanupModel cleanup, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH deleted_receipt AS (
                UPDATE scrapbook.photo_upload_receipts SET deleted_at = {now}
                WHERE storage_object_id = {cleanup.StorageObjectId} AND version_id = {cleanup.VersionId} AND deleted_at IS NULL
                RETURNING storage_object_id, version_id
            )
            UPDATE scrapbook.storage_objects AS object SET state = 'deleted', deleted_at = {now}
            FROM deleted_receipt AS receipt
            WHERE object.id = receipt.storage_object_id AND object.version_id = receipt.version_id AND object.kind = 'temporary'
            """, cancellationToken));

    private async Task<PhotoSubmissionEntity?> FindAsync(string sql, object[] parameters, CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.SqlQueryRaw<PhotoSubmissionEntity>(sql, parameters)
            .ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    private static NpgsqlParameter Civil(string name, DateTime value) =>
        new(name, NpgsqlDbType.Timestamp) { Value = value };

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> execute)
    {
        try
        {
            return await execute();
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
        catch (PostgresException error) when (error.SqlState.StartsWith("23", StringComparison.Ordinal)
            || error.SqlState == PostgresErrorCodes.RaiseException)
        {
            throw new PostgresWriteRejectedException();
        }
    }
}
