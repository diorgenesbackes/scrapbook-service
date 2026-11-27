using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaAudioResultQueue
{
    Task<IReadOnlyList<MediaAudioResultDelivery>> ReceiveAsync(int limit, CancellationToken cancellationToken);
    Task AcknowledgeAsync(string receiptHandle, CancellationToken cancellationToken);
}
