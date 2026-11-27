using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaConfirmationQueue
{
    Task<IReadOnlyList<MediaConfirmationDelivery>> ReceiveAsync(int limit, CancellationToken cancellationToken);
    Task AcknowledgeAsync(string receiptHandle, CancellationToken cancellationToken);
}
