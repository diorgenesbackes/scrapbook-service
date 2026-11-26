using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class EventWriteTestTransaction : IEventTransaction
{
    public Func<CancellationToken, Task>? BeforeCommitAsync { get; init; }
    public CancellationToken CommitToken { get; private set; }
    public bool Committed { get; private set; }
    public bool Disposed { get; private set; }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        CommitToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeCommitAsync is { } beforeCommit)
        {
            await beforeCommit(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Committed = true;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
