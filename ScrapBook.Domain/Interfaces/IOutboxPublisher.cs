namespace ScrapBook.Domain.Interfaces;

public interface IOutboxPublisher
{
    Task PublishAsync(string type, string body, CancellationToken cancellationToken);
}
