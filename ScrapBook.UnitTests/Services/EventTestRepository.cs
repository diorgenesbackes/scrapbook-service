using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class EventTestRepository : IEventRepository
{
    public EventEntity? Event { get; private set; }
    public EventMemberEntity? Member { get; set; }
    public int Inserts { get; private set; }
    public bool ShowAnotherRow { get; set; }
    public PublicEventModel? PublicEvent { get; set; }
    public string? SearchedCode { get; private set; }
    public CancellationToken PublicLookupToken { get; private set; }

    public Task<PublicEventModel?> FindByCodeAsync(string code, CancellationToken cancellationToken)
    {
        SearchedCode = code;
        PublicLookupToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PublicEvent);
    }

    public Task<EventEntity?> FindCreationAsync(long accountId, byte[] keyHash, CancellationToken cancellationToken) =>
        Task.FromResult(Event is { } item && item.CreatorAccountId == accountId
            && keyHash.SequenceEqual(item.CreationKeyHash ?? []) ? item : null);

    public Task<EventEntity?> FindLinkedAsync(long accountId, long eventId, CancellationToken cancellationToken) =>
        Task.FromResult(Event?.Id == eventId && Member?.AccountId == accountId ? Event : null);

    public Task InsertAsync(EventEntity item, EventMemberEntity member, CancellationToken cancellationToken)
    {
        Inserts++;
        item.Id = 11;
        Event = item;
        Member = member;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EventEntity>> ListLinkedAsync(long accountId, long? beforeId, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<EventEntity> rows = Event is { } item && Member?.AccountId == accountId
            && (beforeId is null || item.Id < beforeId) ? (ShowAnotherRow ? [item, item] : [item]) : [];
        return Task.FromResult(rows);
    }
}
