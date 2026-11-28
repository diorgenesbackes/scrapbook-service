using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class MediaHistoryRepository(MediaPreparationRepository repository) : IMediaHistoryRepository
{
    public async Task<IReadOnlyList<MediaHistoryCandidateModel>> FindPageAsync(long eventId, long afterId, int limit,
        CancellationToken cancellationToken) =>
        await repository.QueryListAsync<MediaHistoryCandidateModel>($"""
            SELECT r.id AS "RecordId", f.id AS "FileId"
            FROM scrapbook.records AS r
            JOIN scrapbook.files AS f ON f.files_records_id = r.id
            WHERE r.records_events_id = {eventId} AND r.confirmed_at IS NOT NULL AND f.id > {afterId}
            ORDER BY f.id
            LIMIT {limit}
            """, cancellationToken);

    public Task<MediaSourceEntity?> FindSourceAsync(long eventId, long recordId, long fileId, bool lockFile,
        CancellationToken cancellationToken) =>
        repository.FindSourceAsync(eventId, recordId, fileId, lockFile, cancellationToken);

    public Task<MediaHistoryReceiptModel?> FindReceiptAsync(long eventId, long recordId, CancellationToken cancellationToken) =>
        repository.QueryAsync<MediaHistoryReceiptModel>($"""
            SELECT r.format AS "Format", o.operation AS "Operation", o.state AS "OperationState",
                o.completed_at AS "CompletedAt", s.state AS "SessionState", s.terminal_at AS "TerminalAt",
                s.request_correlation_id AS "CorrelationId"
            FROM scrapbook.records AS r
            JOIN scrapbook.upload_sessions AS s ON s.upload_sessions_records_id = r.id
                AND s.upload_sessions_events_id = r.records_events_id
            JOIN scrapbook.idempotent_operations AS o ON o.id = s.upload_sessions_idempotent_operations_id
                AND o.idempotent_operations_events_id = r.records_events_id AND o.idempotent_operations_records_id = r.id
            WHERE r.records_events_id = {eventId} AND r.id = {recordId}
            """, cancellationToken);

    public async Task<MediaHistoryWorkModel> FindWorkAsync(long fileId, string kind, int profileVersion,
        CancellationToken cancellationToken) =>
        (await repository.QueryListAsync<MediaHistoryWorkModel>($"""
            SELECT j.id AS "JobId", j.confirmation_message_id AS "MessageId",
                j.confirmation_correlation_id AS "CorrelationId", j.confirmed_at AS "ConfirmedAt",
                EXISTS (SELECT 1 FROM scrapbook.file_variants AS v
                    WHERE v.file_variants_files_id = target.file_id AND v.profile_version = {profileVersion}) AS "HasVariants"
            FROM (VALUES ({fileId})) AS target(file_id)
            LEFT JOIN scrapbook.processing_jobs AS j ON j.processing_jobs_files_id = target.file_id
                AND j.kind = {kind} AND j.profile_version = {profileVersion}
            """, cancellationToken)).Single();

    public Task<bool> InsertJobAsync(MediaConfirmationModel confirmation, int profileVersion, string kind, string profile,
        string[] variantKinds, DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.InsertJobAsync(confirmation, profileVersion, kind, profile, variantKinds, true, now, cancellationToken);
}
