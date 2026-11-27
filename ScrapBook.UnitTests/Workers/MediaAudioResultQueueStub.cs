using System.Collections.Concurrent;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Workers;

internal sealed class MediaAudioResultQueueStub(params MediaAudioResultDelivery[] deliveries) : IMediaAudioResultQueue
{
    private int received;
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> Acknowledgements { get; } = new();
    public Action? OnAcknowledgement { get; init; }

    public async Task<IReadOnlyList<MediaAudioResultDelivery>> ReceiveAsync(int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref received) == 1)
            return deliveries;

        Finished.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return [];
    }

    public Task AcknowledgeAsync(string receiptHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnAcknowledgement?.Invoke();
        Acknowledgements.Enqueue(receiptHandle);
        return Task.CompletedTask;
    }
}
