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

public sealed class PhotoSubmissionRepository(ScrapBookDbContext context) : IPhotoSubmissionRepository, IVideoSubmissionRepository
{
    private const string SelectSubmission = """
        SELECT session.id AS "SessionId", record.records_events_id AS "EventId", record.id AS "RecordId",
            file.id AS "FileId", file.position::integer AS "Position",
            temporary.id AS "TemporaryObjectId", original.id AS "OriginalObjectId",
            session.state AS "State", temporary.bucket AS "Bucket",
            temporary.object_key AS "TemporaryKey", original.object_key AS "OriginalKey",
            temporary.version_id AS "TemporaryVersion", temporary.receipt_event_at AS "ReceiptAt",
            temporary.multipart_id AS "MultipartId", file.multipart_checksums AS "PartSha256",
            record.format AS "Format", record.category AS "Category",
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

    public Task InsertAsync(CreatePhotoSubmissionCommand command, CancellationToken cancellationToken)
    {
        var files = command.Files;
        var fileHashes = new NpgsqlParameter("file_hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            Value = files.Select(file => file.Manifest.Sha256).ToArray()
        };
        var partHashes = new NpgsqlParameter("part_hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            Value = (object?)files.SingleOrDefault(file => file.Manifest.PartSha256 is not null)?.Manifest.PartSha256
                ?? DBNull.Value
        };
        return ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH file_intents AS (
                SELECT position, client_key, declared_type, declared_size, sha256, temporary_key, original_key
                FROM unnest(
                    {files.Select(file => file.Position).ToArray()}, {files.Select(file => file.Manifest.FileKey).ToArray()},
                    {files.Select(file => file.Manifest.DeclaredMediaType).ToArray()},
                    {files.Select(file => file.Manifest.SizeBytes).ToArray()}, {fileHashes},
                    {files.Select(file => file.TemporaryKey).ToArray()}, {files.Select(file => file.OriginalKey).ToArray()}
                ) AS intent(position, client_key, declared_type, declared_size, sha256, temporary_key, original_key)
            ), inserted_record AS (
                INSERT INTO scrapbook.records (
                    records_events_id, records_consent_notices_id, category, format, sender_name, caption,
                    expected_file_count, consented_at, submitted_at, state
                ) VALUES (
                    {command.Event.Id}, {command.NoticeId}, {command.Category}, {command.Format}, {command.GuestName}, {command.Caption},
                    {files.Count}, {Civil("consented_at", command.ConsentedAt)}, {Civil("submitted_at", command.SubmittedAt)},
                    'receiving'
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
            ), inserted_files AS (
                INSERT INTO scrapbook.files (
                    files_records_id, position, client_key, kind, declared_type, declared_size, sha256,
                    multipart_checksums, validation_state, processing_state
                )
                SELECT session.upload_sessions_records_id, intent.position, intent.client_key, {command.Format},
                    intent.declared_type, intent.declared_size, intent.sha256, {partHashes}, 'expected', 'not_requested'
                FROM inserted_session AS session
                CROSS JOIN file_intents AS intent
                ORDER BY intent.position
                RETURNING id, position
            )
            INSERT INTO scrapbook.storage_objects (storage_objects_files_id, kind, state, bucket, object_key)
            SELECT file.id, object.kind, 'planned', {command.Bucket}, object.key
            FROM inserted_files AS file
            JOIN file_intents AS intent ON intent.position = file.position
            CROSS JOIN LATERAL (VALUES ('temporary', intent.temporary_key), ('original', intent.original_key)) AS object(kind, key)
            """, cancellationToken));
    }

    public async Task<long?> TryBeginMultipartAsync(long objectId, long sessionId, DateTimeOffset now, DateTimeOffset retryBefore,
        CancellationToken cancellationToken)
    {
        var versions = await ExecuteAsync(() => context.Database.SqlQuery<long>($"""
            UPDATE scrapbook.storage_objects AS object
            SET state = 'initializing', observed_at = {now}, version = object.version + 1
            FROM scrapbook.files AS file, scrapbook.upload_sessions AS session
            WHERE object.id = {objectId} AND object.kind = 'temporary' AND object.multipart_id IS NULL
                AND object.version_id IS NULL AND (object.state = 'planned'
                    OR (object.state = 'initializing' AND object.observed_at <= {retryBefore}))
                AND file.id = object.storage_objects_files_id AND session.id = {sessionId}
                AND session.upload_sessions_records_id = file.files_records_id AND session.state = 'receiving'
                AND session.completion_deadline_at > ({now} AT TIME ZONE 'America/Sao_Paulo')
            RETURNING object.version AS "Value"
            """).ToListAsync(cancellationToken));
        return versions.Count == 0 ? null : versions.Single();
    }

    public async Task<bool> TrySetMultipartIdAsync(long objectId, long sessionId, long claimVersion, string multipartId,
        CancellationToken cancellationToken)
    {
        var count = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.storage_objects AS object
            SET multipart_id = {multipartId}, state = 'uploading', version = object.version + 1
            FROM scrapbook.files AS file, scrapbook.upload_sessions AS session
            WHERE object.id = {objectId} AND object.kind = 'temporary' AND object.multipart_id IS NULL
                AND object.version_id IS NULL AND object.state = 'initializing' AND object.version = {claimVersion}
                AND file.id = object.storage_objects_files_id AND session.id = {sessionId}
                AND session.upload_sessions_records_id = file.files_records_id AND session.state = 'receiving'
            """, cancellationToken));
        return count == 1;
    }

    public Task<PhotoSubmissionEntity?> FindForRecordAsync(long recordId, Guid fileKey, CancellationToken cancellationToken) =>
        FindAsync(SelectSubmission + " WHERE record.id = {0} AND file.client_key = {1}", [recordId, fileKey], cancellationToken);

    public async Task<IReadOnlyList<PhotoSubmissionEntity>> FindFilesAsync(long recordId, CancellationToken cancellationToken) =>
        await ExecuteAsync(() => context.Database.SqlQueryRaw<PhotoSubmissionEntity>(
            SelectSubmission + " WHERE record.id = {0} ORDER BY file.position", recordId).ToListAsync(cancellationToken));

    public async Task<PhotoSubmissionEntity?> FindForObjectAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        var sessions = await ExecuteAsync(() => context.Database.SqlQuery<long>($"""
            SELECT session.id AS "Value"
            FROM scrapbook.upload_sessions AS session
            JOIN scrapbook.files AS file ON file.files_records_id = session.upload_sessions_records_id
            JOIN scrapbook.storage_objects AS temporary ON temporary.storage_objects_files_id = file.id AND temporary.kind = 'temporary'
            WHERE temporary.bucket = {bucket} AND temporary.object_key = {key}
            FOR UPDATE OF session
            """).ToListAsync(cancellationToken));
        return sessions.Count == 0 ? null : await FindAsync(
            SelectSubmission + " WHERE session.id = {0} AND temporary.bucket = {1} AND temporary.object_key = {2}",
            [sessions.Single(), bucket, key], cancellationToken);
    }

    public Task<PhotoSubmissionEntity?> FindWorkAsync(DateTimeOffset now, DateTime expiryBefore, CancellationToken cancellationToken) =>
        FindAsync(SelectSubmission + """
             WHERE session.next_attempt_at <= {0} AND (session.lease_until IS NULL OR session.lease_until <= {0})
                AND (session.state = 'validating' OR (session.state = 'receiving' AND session.completion_deadline_at <= {1}))
             ORDER BY session.next_attempt_at, session.id, file.position LIMIT 1
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
            INSERT INTO scrapbook.photo_upload_receipts (storage_object_id, version_id, received_at, event_kind, size, cleanup_after)
            VALUES ({storageObjectId}, {receipt.VersionId}, {receipt.ReceivedAt}, {receipt.EventKind}, {receipt.Size}, {cleanupAfter})
            ON CONFLICT (storage_object_id, version_id) DO NOTHING
            """, cancellationToken));

    public async Task PinAsync(PhotoSubmissionEntity submission, PhotoReceiptModel receipt, DateTimeOffset now, bool seal,
        CancellationToken cancellationToken)
    {
        var count = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH updated_session AS (
                UPDATE scrapbook.upload_sessions
                SET state = CASE WHEN {seal} THEN 'validating' ELSE state END,
                    next_attempt_at = CASE WHEN {seal} THEN {now} ELSE next_attempt_at END,
                    sealed_at = CASE WHEN {seal} THEN {now} ELSE sealed_at END,
                    lease_token = NULL, lease_until = NULL
                WHERE id = {submission.SessionId} AND state = 'receiving'
                RETURNING id
            ), updated_record AS (
                UPDATE scrapbook.records SET state = 'validating'
                WHERE id = {submission.RecordId} AND {seal} AND EXISTS (SELECT 1 FROM updated_session)
            ), updated_file AS (
                UPDATE scrapbook.files SET validation_state = CASE WHEN {seal} THEN 'validating' ELSE 'received' END
                WHERE files_records_id = {submission.RecordId} AND ({seal} OR id = {submission.FileId})
                    AND EXISTS (SELECT 1 FROM updated_session)
            )
            UPDATE scrapbook.storage_objects
            SET state = 'uploading', version_id = {receipt.VersionId}, receipt_event_at = {receipt.ReceivedAt},
                receipt_event_kind = {receipt.EventKind}, observed_at = {now}
            WHERE id = {submission.TemporaryObjectId} AND storage_objects_files_id = {submission.FileId}
                AND kind = 'temporary' AND version_id IS NULL AND EXISTS (SELECT 1 FROM updated_session)
            """, cancellationToken));
        if (count != 1)
        {
            throw new InvalidOperationException("Receipt did not match the receiving session.");
        }
    }

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

    public async Task<DateTimeOffset?> FindReceiptAtAsync(long objectId, string versionId, string eventKind,
        CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.SqlQuery<DateTimeOffset>($"""
            SELECT received_at AS "Value" FROM scrapbook.photo_upload_receipts
            WHERE storage_object_id = {objectId} AND version_id = {versionId} AND event_kind = {eventKind}
            """).ToListAsync(cancellationToken));
        return rows.Count == 0 ? null : rows.Single();
    }

    public async Task<bool> ConfirmAsync(ConfirmPhotoSubmissionCommand command, CancellationToken cancellationToken)
    {
        var submission = command.Submission;
        var count = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH updated_session AS (
                UPDATE scrapbook.upload_sessions
                SET state = 'confirmed', terminal_at = {command.CommittedAt}, lease_token = NULL, lease_until = NULL,
                    last_error_code = NULL
                WHERE id = {submission.SessionId} AND upload_sessions_records_id = {submission.RecordId}
                    AND lease_token = {command.Lease} AND lease_until > {command.CommittedAt} AND state = 'validating'
                    AND EXISTS (SELECT 1 FROM scrapbook.records
                        WHERE id = {submission.RecordId} AND expected_file_count = {command.Files.Count})
                RETURNING upload_sessions_idempotent_operations_id
            ), updated_record AS (
                UPDATE scrapbook.records
                SET state = 'confirmed', received_at = {Civil("record_received", command.ReceivedAt)},
                    confirmed_at = {Civil("record_confirmed", command.ConfirmedAt)}
                WHERE id = {submission.RecordId} AND EXISTS (SELECT 1 FROM updated_session)
            )
            UPDATE scrapbook.idempotent_operations
            SET state = 'confirmed', completed_at = {command.CommittedAt}
            WHERE id IN (SELECT upload_sessions_idempotent_operations_id FROM updated_session)
            """, cancellationToken));
        if (count != 1) return false;

        foreach (var file in command.Files)
        {
            if (!await ConfirmFileAsync(submission.RecordId, file, command.ConfirmedAt, command.CommittedAt, cancellationToken))
            {
                return false;
            }
        }
        return true;
    }

    private async Task<bool> ConfirmFileAsync(long recordId, ConfirmPhotoFileModel file, DateTime confirmedAt,
        DateTimeOffset committedAt, CancellationToken cancellationToken)
    {
        var count = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH updated_file AS (
                UPDATE scrapbook.files
                SET actual_size = {file.Original.Size}, media_type = {file.Image?.MediaType ?? file.Video?.MediaType ?? file.Audio?.MediaType},
                    container = {file.Image?.Container ?? file.Video?.Container ?? file.Audio?.Container},
                    width = {file.Image?.Width ?? file.Video?.Width}, height = {file.Image?.Height ?? file.Video?.Height},
                    orientation = {file.Image?.Orientation}, rotation = {(short?)file.Video?.Rotation},
                    video_codec = {file.Video?.VideoCodec}, audio_codec = {file.Video?.AudioCodec ?? file.Audio?.AudioCodec},
                    duration = {file.Video?.Duration ?? file.Audio?.Duration},
                    frame_rate_numerator = {file.Video?.FrameRateNumerator}, frame_rate_denominator = {file.Video?.FrameRateDenominator},
                    validation_state = 'valid', processing_state = 'pending',
                    received_at = {Civil("file_received", file.ReceivedAt)}, validated_at = {committedAt},
                    confirmed_at = {Civil("file_confirmed", confirmedAt)}
                WHERE id = {file.File.FileId} AND files_records_id = {recordId} AND validation_state = 'validating'
                    AND EXISTS (SELECT 1 FROM scrapbook.storage_objects AS temporary
                        WHERE temporary.id = {file.File.TemporaryObjectId} AND temporary.storage_objects_files_id = {file.File.FileId}
                            AND temporary.kind = 'temporary' AND temporary.version_id = {file.File.TemporaryVersion})
                RETURNING id
            )
            UPDATE scrapbook.storage_objects
            SET state = 'stored', version_id = {file.Original.VersionId}, size = {file.Original.Size},
                sha256 = {file.File.Sha256}, last_modified_at = {file.Original.LastModifiedAt},
                observed_at = {committedAt}, stored_at = {committedAt}
            WHERE id = {file.File.OriginalObjectId} AND kind = 'original' AND state = 'planned'
                AND storage_objects_files_id IN (SELECT id FROM updated_file)
            """, cancellationToken));
        return count == 1;
    }

    public Task TerminateAsync(PhotoSubmissionEntity submission, Guid lease, string state, string code,
        IReadOnlyDictionary<long, string> problems, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH updated_session AS (
                UPDATE scrapbook.upload_sessions
                SET state = {state}, terminal_at = {now}, lease_token = NULL, lease_until = NULL, last_error_code = {code}
                WHERE id = {submission.SessionId} AND lease_token = {lease} AND lease_until > {now} AND state = {submission.State}
                RETURNING upload_sessions_idempotent_operations_id
            ), updated_record AS (
                UPDATE scrapbook.records SET state = 'rejected', rejection_code = {code}
                WHERE id = {submission.RecordId} AND EXISTS (SELECT 1 FROM updated_session)
            ), updated_file AS (
                UPDATE scrapbook.files
                SET validation_state = 'rejected', error_code = (
                    SELECT problem.code FROM unnest({problems.Keys.ToArray()}, {problems.Values.ToArray()}) AS problem(file_id, code)
                    WHERE problem.file_id = files.id
                )
                WHERE files_records_id = {submission.RecordId} AND EXISTS (SELECT 1 FROM updated_session)
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
                UPDATE scrapbook.photo_upload_receipts AS receipt SET deleted_at = {now}
                FROM scrapbook.storage_objects AS temporary
                WHERE receipt.storage_object_id = {cleanup.StorageObjectId} AND receipt.version_id = {cleanup.VersionId}
                    AND receipt.deleted_at IS NULL AND temporary.id = receipt.storage_object_id AND temporary.kind = 'temporary'
                    AND temporary.bucket = {cleanup.Bucket} AND temporary.object_key = {cleanup.Key}
                RETURNING receipt.storage_object_id, receipt.version_id
            )
            UPDATE scrapbook.storage_objects AS object SET state = 'deleted', deleted_at = {now}
            FROM deleted_receipt AS receipt
            WHERE object.id = receipt.storage_object_id AND object.version_id = receipt.version_id AND object.kind = 'temporary'
            """, cancellationToken));

    public Task<PhotoSubmissionEntity?> FindMultipartCleanupAsync(DateTimeOffset now, DateTimeOffset terminalBefore,
        string[] terminalStates, string deadlineOnlyState, CancellationToken cancellationToken) =>
        FindAsync(SelectSubmission + """
             LEFT JOIN scrapbook.cleanup_jobs AS job
                ON job.cleanup_jobs_storage_objects_id = temporary.id AND job.action = 'abort_multipart'
             WHERE file.multipart_checksums IS NOT NULL AND session.state = ANY({2})
                AND session.completion_deadline_at <= ({1} AT TIME ZONE 'America/Sao_Paulo')
                AND (session.state = {3} OR session.terminal_at <= {1})
                AND (job.id IS NULL OR (job.state IN ('pending', 'running', 'reconciling')
                    AND job.eligible_at <= {0} AND job.next_attempt_at <= {0}
                    AND (job.lease_until IS NULL OR job.lease_until <= {0})))
             ORDER BY COALESCE(job.next_attempt_at, session.terminal_at), session.id LIMIT 1
            """, [now, terminalBefore, terminalStates, deadlineOnlyState], cancellationToken);

    public async Task<bool> TryClaimMultipartCleanupAsync(long objectId, DateTimeOffset eligibleAt, DateTimeOffset now,
        Guid lease, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        var count = await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scrapbook.cleanup_jobs (
                cleanup_jobs_storage_objects_id, action, eligible_at, state, next_attempt_at, attempt_count, lease_token, lease_until
            )
            SELECT temporary.id, 'abort_multipart', {eligibleAt}, 'running', {now}, 1, {lease}, {leaseUntil}
            FROM scrapbook.storage_objects AS temporary
            WHERE temporary.id = {objectId} AND temporary.kind = 'temporary' AND {eligibleAt} <= {now}
            ON CONFLICT (cleanup_jobs_storage_objects_id, action) DO UPDATE
            SET state = 'running', lease_token = EXCLUDED.lease_token, lease_until = EXCLUDED.lease_until,
                attempt_count = cleanup_jobs.attempt_count + 1
            WHERE cleanup_jobs.state IN ('pending', 'running', 'reconciling')
                AND cleanup_jobs.eligible_at <= {now} AND cleanup_jobs.next_attempt_at <= {now}
                AND (cleanup_jobs.lease_until IS NULL OR cleanup_jobs.lease_until <= {now})
            """, cancellationToken));
        return count == 1;
    }

    public Task MarkMultipartAbortedAsync(long objectId, Guid lease, DateTimeOffset completedAt, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.cleanup_jobs
            SET state = 'completed', next_attempt_at = {completedAt}, completed_at = {completedAt},
                last_error_code = NULL, lease_token = NULL, lease_until = NULL
            WHERE cleanup_jobs_storage_objects_id = {objectId} AND action = 'abort_multipart'
                AND state = 'running' AND lease_token = {lease} AND lease_until > {completedAt}
            """, cancellationToken));

    public Task ScheduleMultipartCleanupAsync(long objectId, Guid lease, DateTimeOffset now, DateTimeOffset nextAttemptAt,
        string errorCode, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.cleanup_jobs
            SET state = 'reconciling', next_attempt_at = {nextAttemptAt}, last_error_code = {errorCode},
                lease_token = NULL, lease_until = NULL
            WHERE cleanup_jobs_storage_objects_id = {objectId} AND action = 'abort_multipart'
                AND state = 'running' AND lease_token = {lease} AND lease_until > {now}
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
