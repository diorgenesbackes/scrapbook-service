using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

public sealed class EventStructuralLockMonitoringService(IOutboxRepository outbox, IEventStructuralLockRepository locks)
{
    public Task<OutboxBacklogModel> ReadBacklogAsync(CancellationToken cancellationToken) =>
        outbox.GetBacklogAsync(FirstRecordConfirmedModel.Type, cancellationToken);

    public Task<bool> HasUnmatchedConfirmationsAsync(CancellationToken cancellationToken) =>
        locks.HasUnmatchedConfirmationsAsync(FirstRecordConfirmedModel.Type, FirstRecordConfirmedModel.Version, cancellationToken);
}
