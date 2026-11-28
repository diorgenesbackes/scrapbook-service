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
                a.output_prefix AS "OutputPrefix", f.kind AS "FileKind"
            FROM scrapbook.storage_objects AS o
            JOIN scrapbook.processing_attempts AS a ON a.id = o.storage_objects_processing_attempts_id
                AND a.processing_attempts_files_id = o.storage_objects_files_id
            JOIN scrapbook.files AS f ON f.id = o.storage_objects_files_id
            JOIN scrapbook.records AS r ON r.id = f.files_records_id
            WHERE r.records_events_id = {eventId} AND o.id > {afterId} AND o.kind = 'derived'
                AND (o.state IN ('planned', 'stored') OR (o.state = 'deleting' AND o.updated_at <= {cutoff.AddHours(24).AddMinutes(-10)}))
                AND a.state IN ('failed', 'succeeded') AND a.finished_at <= {cutoff}
                AND (a.state = 'failed' OR (a.provider = 'mediaconvert' AND o.object_key LIKE 'submissions/transcode-input/%'))
                AND NOT EXISTS (
                    SELECT 1 FROM scrapbook.file_variants AS v
                    WHERE v.file_variants_storage_objects_id = o.id AND v.state = 'ready'
                )
            ORDER BY o.id LIMIT {limit}
            """).ToListAsync(cancellationToken);

    public Task<long?> ClaimAsync(long eventId, MediaCleanupObjectModel candidate, DateTimeOffset cutoff, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        repository.QueryAsync<long?>($"""
            UPDATE scrapbook.storage_objects AS o
            SET state = 'deleting', updated_at = {now}, version = o.version + 1
            FROM scrapbook.processing_attempts AS a, scrapbook.files AS f, scrapbook.records AS r
            WHERE o.id = {candidate.Id} AND o.version = {candidate.Version} AND o.kind = 'derived'
                AND o.bucket = {candidate.Bucket} AND o.object_key = {candidate.ObjectKey}
                AND (o.state IN ('planned', 'stored') OR (o.state = 'deleting' AND o.updated_at <= {now.AddMinutes(-10)}))
                AND a.id = o.storage_objects_processing_attempts_id AND a.processing_attempts_files_id = o.storage_objects_files_id
                AND a.provider = {candidate.Provider} AND a.state = {candidate.AttemptState}
                AND a.request_key = {candidate.RequestKey} AND a.output_prefix = {candidate.OutputPrefix} AND a.finished_at <= {cutoff}
                AND f.id = o.storage_objects_files_id AND f.id = {candidate.FileId} AND f.kind = {candidate.FileKind}
                AND r.id = f.files_records_id AND r.records_events_id = {eventId}
                AND NOT EXISTS (
                    SELECT 1 FROM scrapbook.file_variants AS v
                    WHERE v.file_variants_storage_objects_id = o.id AND v.state = 'ready'
                )
            RETURNING o.version AS "Value"
            """, cancellationToken);

    public async Task<bool> CompleteAsync(long objectId, long version, DateTimeOffset now, CancellationToken cancellationToken) =>
        await repository.QueryAsync<long?>($"""
            UPDATE scrapbook.storage_objects AS o
            SET state = 'deleted', deleted_at = {now}, updated_at = {now}, version = o.version + 1
            WHERE o.id = {objectId} AND o.version = {version} AND o.state = 'deleting'
            RETURNING o.id AS "Value"
            """, cancellationToken) is not null;
}
