using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Workers;

internal sealed class EventStructuralLockTestQueue(params EventStructuralLockDelivery[] deliveries) : IEventStructuralLockQueue
{
    private readonly Queue<EventStructuralLockDelivery> pending = new(deliveries);
    public TaskCompletionSource PolledAfterDelivery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Acknowledgements { get; } = [];
    public Action? OnAcknowledge { get; set; }

    public async Task<IReadOnlyList<EventStructuralLockDelivery>> ReceiveAsync(int limit, CancellationToken cancellationToken)
    {
        if (pending.TryDequeue(out var delivery))
        {
            return [delivery];
        }

        PolledAfterDelivery.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return [];
    }

    public Task AcknowledgeAsync(string receiptHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Acknowledgements.Add(receiptHandle);
        OnAcknowledge?.Invoke();
        return Task.CompletedTask;
    }
}
