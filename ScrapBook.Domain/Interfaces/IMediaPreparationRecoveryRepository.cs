using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaPreparationRecoveryRepository
{
    Task<OutboxMessageEntity?> FindFactAsync(long eventId, Guid messageId, CancellationToken cancellationToken);
    Task<ProcessedMessageEntity?> FindDeliveryAsync(long eventId, Guid messageId, string consumer, string source,
        CancellationToken cancellationToken);
    Task<MediaRecoveryJobEntity?> FindJobAsync(long eventId, long fileId, int profileVersion, bool lockJob,
        CancellationToken cancellationToken);
    Task<string[]?> FindFailedVariantsAsync(long eventId, long fileId, int profileVersion, CancellationToken cancellationToken);
    Task ReopenAsync(long eventId, MediaRecoveryJobEntity job, int profileVersion, int nextCycle, string[] variantKinds,
        DateTimeOffset now, CancellationToken cancellationToken);
}
