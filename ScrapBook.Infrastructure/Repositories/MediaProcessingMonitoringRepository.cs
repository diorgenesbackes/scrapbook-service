using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class MediaProcessingMonitoringRepository(MediaPreparationRepository repository) : IMediaProcessingMonitoringRepository
{
    public async Task<IReadOnlyList<MediaProcessingBacklogModel>> GetProcessingBacklogAsync(CancellationToken cancellationToken) =>
        await repository.QueryListAsync<MediaProcessingBacklogModel>($"""
            WITH backlog AS (
                SELECT f.kind, j.is_historical,
                    count(*) FILTER (WHERE j.state = 'pending') AS pending,
                    count(*) FILTER (WHERE j.state = 'running') AS active,
                    count(*) FILTER (WHERE j.state = 'reconciling') AS reconciling,
                    min(CASE WHEN j.is_historical THEN j.created_at ELSE COALESCE(j.confirmed_at, j.created_at) END) AS oldest
                FROM scrapbook.processing_jobs AS j
                JOIN scrapbook.files AS f ON f.id = j.processing_jobs_files_id AND f.profile_version = j.profile_version
                WHERE j.state IN ('pending', 'running', 'reconciling')
                GROUP BY f.kind, j.is_historical
            )
            SELECT media.kind AS "MediaType", cohort.historical AS "IsHistorical",
                COALESCE(backlog.pending, 0) AS "PendingCount", COALESCE(backlog.active, 0) AS "ActiveCount",
                COALESCE(backlog.reconciling, 0) AS "ReconcilingCount", backlog.oldest AS "OldestPendingAt"
            FROM (VALUES ('photo'), ('audio'), ('video')) AS media(kind)
            CROSS JOIN (VALUES (false), (true)) AS cohort(historical)
            LEFT JOIN backlog ON backlog.kind = media.kind AND backlog.is_historical = cohort.historical
            """, cancellationToken);
}
