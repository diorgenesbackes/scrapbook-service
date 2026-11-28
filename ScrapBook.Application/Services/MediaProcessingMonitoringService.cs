using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

public sealed class MediaProcessingMonitoringService(IOutboxRepository outbox, IMediaProcessingMonitoringRepository processing)
{
    public Task<OutboxBacklogModel> ReadBacklogAsync(CancellationToken cancellationToken) =>
        outbox.GetBacklogAsync(MediaConfirmationModel.Type, cancellationToken);

    public Task<IReadOnlyList<MediaProcessingBacklogModel>> ReadProcessingBacklogAsync(CancellationToken cancellationToken) =>
        processing.GetProcessingBacklogAsync(cancellationToken);
}
