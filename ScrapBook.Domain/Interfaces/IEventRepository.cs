using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IEventRepository
{
    Task<IReadOnlyList<EventEntity>> ListLinkedAsync(long accountId, long? beforeId, int limit, CancellationToken cancellationToken);
    Task<EventEntity?> FindLinkedAsync(long accountId, long eventId, CancellationToken cancellationToken);
    Task<PublicEventModel?> FindByCodeAsync(string code, CancellationToken cancellationToken);
    Task<EventEntity?> FindCreationAsync(long accountId, byte[] keyHash, CancellationToken cancellationToken);
    Task InsertAsync(EventEntity item, EventMemberEntity member, CancellationToken cancellationToken);
}
