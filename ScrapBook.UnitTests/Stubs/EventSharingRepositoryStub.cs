using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Stubs;

internal sealed class EventSharingRepositoryStub(EventSharingModel? model) : IEventSharingRepository
{
    public long AccountId { get; private set; }
    public long EventId { get; private set; }
    public CancellationToken Token { get; private set; }

    public Task<EventSharingModel?> GetAsync(long accountId, long eventId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AccountId = accountId; EventId = eventId; Token = cancellationToken;
        return Task.FromResult(model);
    }
}
