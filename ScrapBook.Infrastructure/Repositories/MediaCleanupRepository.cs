using Microsoft.EntityFrameworkCore;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class MediaCleanupRepository(ScrapBookDbContext context, MediaPreparationRepository repository)
    : IMediaCleanupRepository
{
    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken) => repository.BeginAsync(cancellationToken);

    public async Task<IReadOnlyList<MediaCleanupObjectModel>> FindCandidatesAsync(long eventId, long afterId, int limit,
        DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        await context.Database.SqlQuery<MediaCleanupObjectModel>($"""
            SELECT o.id AS "Id", f.id AS "FileId", o.version AS "Version", o.bucket AS "Bucket", o.object_key AS "ObjectKey",
                a.provider AS "Provider", a.state AS "AttemptState", a.request_key AS "RequestKey",
                a.output_prefix AS "OutputPrefix", f.kind AS "FileKind",
                COALESCE(c.artifact_version_id, o.version_id) AS "VersionId", o.last_modified_at AS "LastModified"
            FROM scrapbook.storage_objects AS o
            JOIN scrapbook.processing_attempts AS a ON a.id = o.storage_objects_processing_attempts_id
                AND a.processing_attempts_files_id = o.storage_objects_files_id
            JOIN scrapbook.files AS f ON f.id = o.storage_objects_files_id
            JOIN scrapbook.records AS r ON r.id = f.files_records_id
            LEFT JOIN scrapbook.cleanup_jobs AS c ON c.cleanup_jobs_storage_objects_id = o.id AND c.action = 'delete_object'
            WHERE r.records_events_id = {eventId} AND o.id > {afterId} AND o.kind = 'derived'
                AND o.state IN ('planned', 'stored', 'deleting')
                AND (c.id IS NULL OR (c.state IN ('pending', 'running', 'reconciling')
                    AND c.next_attempt_at <= {cutoff.AddHours(24)} AND (c.lease_until IS NULL OR c.lease_until <= {cutoff.AddHours(24)})))
                AND a.state IN ('failed', 'succeeded') AND a.finished_at <= {cutoff}
                AND (a.state = 'failed' OR (a.provider = 'mediaconvert' AND o.object_key LIKE 'submissions/transcode-input/%'))
                AND NOT EXISTS (
                    SELECT 1 FROM scrapbook.file_variants AS v
                    WHERE v.file_variants_storage_objects_id = o.id AND v.state = 'ready'
                )
            ORDER BY o.id LIMIT {limit}
            """).ToListAsync(cancellationToken);

    public Task<long?> ClaimAsync(long eventId, MediaCleanupObjectModel candidate, string versionId, Guid lease,
        DateTimeOffset cutoff, DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.QueryAsync<long?>($"""
            WITH target AS (
                UPDATE scrapbook.storage_objects AS o
                SET state = 'deleting', updated_at = {now}, version = o.version + 1
                FROM scrapbook.processing_attempts AS a, scrapbook.files AS f, scrapbook.records AS r
                WHERE o.id = {candidate.Id} AND o.version = {candidate.Version} AND o.kind = 'derived'
                    AND o.bucket = {candidate.Bucket} AND o.object_key = {candidate.ObjectKey}
                    AND o.state IN ('planned', 'stored', 'deleting')
                    AND (o.version_id IS NULL OR o.version_id = {versionId} AND o.last_modified_at <= {cutoff})
                    AND a.id = o.storage_objects_processing_attempts_id AND a.processing_attempts_files_id = o.storage_objects_files_id
                    AND a.provider = {candidate.Provider} AND a.state = {candidate.AttemptState}
                    AND a.request_key = {candidate.RequestKey} AND a.output_prefix = {candidate.OutputPrefix} AND a.finished_at <= {cutoff}
                    AND f.id = o.storage_objects_files_id AND f.id = {candidate.FileId} AND f.kind = {candidate.FileKind}
                    AND r.id = f.files_records_id AND r.records_events_id = {eventId}
                    AND NOT EXISTS (
                        SELECT 1 FROM scrapbook.file_variants AS v
                        WHERE v.file_variants_storage_objects_id = o.id AND v.state = 'ready'
                    )
                RETURNING o.id, o.version
            ), claimed AS (
                INSERT INTO scrapbook.cleanup_jobs AS c (cleanup_jobs_storage_objects_id, action, artifact_version_id,
                    eligible_at, state, next_attempt_at, attempt_count, lease_token, lease_until)
                SELECT target.id, 'delete_object', {versionId}, {now}, 'running', {now}, 1, {lease}, {now.AddMinutes(10)}
                FROM target
                ON CONFLICT (cleanup_jobs_storage_objects_id, action) DO UPDATE
                SET state = 'running', artifact_version_id = EXCLUDED.artifact_version_id, lease_token = EXCLUDED.lease_token,
                    lease_until = EXCLUDED.lease_until, attempt_count = c.attempt_count + 1, updated_at = {now}, version = c.version + 1
                WHERE c.state IN ('pending', 'running', 'reconciling') AND c.eligible_at <= {now} AND c.next_attempt_at <= {now}
                    AND (c.lease_until IS NULL OR c.lease_until <= {now})
                    AND (c.artifact_version_id IS NULL OR c.artifact_version_id = EXCLUDED.artifact_version_id)
                RETURNING c.cleanup_jobs_storage_objects_id
            )
            SELECT target.version AS "Value" FROM target JOIN claimed ON claimed.cleanup_jobs_storage_objects_id = target.id
            """, cancellationToken);

    public async Task<bool> CompleteAsync(long objectId, long version, Guid lease, DateTimeOffset now,
        CancellationToken cancellationToken) => await repository.QueryAsync<long?>($"""
            WITH deleted AS (
                UPDATE scrapbook.storage_objects AS o
                SET state = 'deleted', deleted_at = {now}, updated_at = {now}, version = o.version + 1
                WHERE o.id = {objectId} AND o.version = {version} AND o.kind = 'derived' AND o.state = 'deleting'
                RETURNING o.id
            )
            UPDATE scrapbook.cleanup_jobs AS c
            SET state = 'completed', completed_at = {now}, updated_at = {now}, version = c.version + 1,
                lease_token = NULL, lease_until = NULL
            FROM deleted
            WHERE c.cleanup_jobs_storage_objects_id = deleted.id AND c.action = 'delete_object'
                AND c.state = 'running' AND c.lease_token = {lease} AND c.lease_until > {now}
            RETURNING c.id AS "Value"
            """, cancellationToken) is not null;
}
