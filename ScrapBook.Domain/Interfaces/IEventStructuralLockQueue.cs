using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IEventStructuralLockQueue
{
    Task<IReadOnlyList<EventStructuralLockDelivery>> ReceiveAsync(int limit, CancellationToken cancellationToken);
    Task AcknowledgeAsync(string receiptHandle, CancellationToken cancellationToken);
}
