using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaProcessingMonitoringRepository
{
    Task<IReadOnlyList<MediaProcessingBacklogModel>> GetProcessingBacklogAsync(DateTimeOffset sampledAt, CancellationToken cancellationToken);
}
