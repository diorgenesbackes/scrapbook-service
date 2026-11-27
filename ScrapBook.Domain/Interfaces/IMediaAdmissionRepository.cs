using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaAdmissionRepository
{
    Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken);
    Task<MediaSourceEntity?> FindSourceAsync(MediaConfirmationModel message, bool lockFile, CancellationToken cancellationToken);
    Task<ProcessedMessageEntity?> FindDeliveryAsync(string consumer, string source, Guid messageId, CancellationToken cancellationToken);
    Task InsertDeliveryAsync(string consumer, string source, MediaConfirmationModel message, byte[] hash,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> InsertJobAsync(MediaConfirmationModel message, int profileVersion, string kind, string profile,
        string[] variantKinds, DateTimeOffset now, CancellationToken cancellationToken);
}
