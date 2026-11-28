using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class MediaProcessingMonitoringRepository(MediaPreparationRepository repository) : IMediaProcessingMonitoringRepository
{
    public async Task<IReadOnlyList<MediaProcessingBacklogModel>> GetProcessingBacklogAsync(DateTimeOffset sampledAt,
        CancellationToken cancellationToken) =>
        await repository.QueryListAsync<MediaProcessingBacklogModel>($"""
            WITH unfinished AS (
                SELECT f.kind, j.is_historical,
                    CASE
                        WHEN j.state IN ('running', 'reconciling') AND j.lease_until > {sampledAt} THEN 'active'
                        WHEN j.state = 'reconciling' THEN 'reconciling'
                        ELSE 'pending'
                    END AS bucket,
                    CASE WHEN j.is_historical THEN j.created_at ELSE COALESCE(j.confirmed_at, j.created_at) END AS age_from
                FROM scrapbook.processing_jobs AS j
                JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
                WHERE j.state IN ('pending', 'running', 'reconciling')
            ), backlog AS (
                SELECT kind, is_historical,
                    count(*) FILTER (WHERE bucket = 'pending') AS pending,
                    count(*) FILTER (WHERE bucket = 'active') AS active,
                    count(*) FILTER (WHERE bucket = 'reconciling') AS reconciling,
                    min(age_from) AS oldest
                FROM unfinished
                GROUP BY kind, is_historical
            )
            SELECT media.kind AS "MediaType", cohort.historical AS "IsHistorical",
                COALESCE(backlog.pending, 0) AS "PendingCount", COALESCE(backlog.active, 0) AS "ActiveCount",
                COALESCE(backlog.reconciling, 0) AS "ReconcilingCount", backlog.oldest AS "OldestPendingAt"
            FROM (VALUES ('photo'), ('audio'), ('video')) AS media(kind)
            CROSS JOIN (VALUES (false), (true)) AS cohort(historical)
            LEFT JOIN backlog ON backlog.kind = media.kind AND backlog.is_historical = cohort.historical
            """, cancellationToken);
}
