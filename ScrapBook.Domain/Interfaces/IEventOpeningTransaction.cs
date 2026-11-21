namespace ScrapBook.Domain.Interfaces;

public interface IEventOpeningTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
