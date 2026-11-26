using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class OutboxTestPublisher : IOutboxPublisher
{
    public Func<CancellationToken, Task> Dispatch { get; set; } = _ => Task.CompletedTask;
    public List<(string Type, string Body, CancellationToken Token)> Messages { get; } = [];

    public Task PublishAsync(string type, string body, CancellationToken cancellationToken)
    {
        Messages.Add((type, body, cancellationToken));
        return Dispatch(cancellationToken);
    }
}
