using ScrapBook.Application.Enums;
using ScrapBook.Application.Models;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;
namespace ScrapBook.Application.Interfaces;

public interface IEventRepository
{
    Task<EventEntity?> GetAsync(long accountId, long eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventListItemModel>> ListAsync(long accountId, int count, EventCursorModel? cursor, CancellationToken cancellationToken);
    Task<EventEntity> CreateAsync(EventEntity state, DateTimeOffset joinedAt, Guid correlationId, CancellationToken cancellationToken);
    Task<EventEntity> MutateAsync(long accountId, long eventId, long expectedVersion, EventMutationEnum mutation,
        Func<EventEntity, EventChangeModel> apply, Guid correlationId, CancellationToken cancellationToken);
}
