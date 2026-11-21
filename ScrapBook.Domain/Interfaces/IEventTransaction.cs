namespace ScrapBook.Domain.Interfaces;

public interface IEventTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
