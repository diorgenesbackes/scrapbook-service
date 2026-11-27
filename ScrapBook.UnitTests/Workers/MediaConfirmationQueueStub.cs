using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Workers;

internal sealed class MediaConfirmationQueueStub(params MediaConfirmationDelivery[] deliveries) : IMediaConfirmationQueue
{
    private readonly Queue<MediaConfirmationDelivery> pending = new(deliveries);

    public List<string> Acknowledgements { get; } = [];
    public Action? OnAcknowledgement { get; set; }
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IReadOnlyList<MediaConfirmationDelivery>> ReceiveAsync(int limit, CancellationToken cancellationToken)
    {
        if (pending.TryDequeue(out var delivery)) return [delivery];
        Finished.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return [];
    }

    public Task AcknowledgeAsync(string receiptHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Acknowledgements.Add(receiptHandle);
        OnAcknowledgement?.Invoke();
        return Task.CompletedTask;
    }
}
