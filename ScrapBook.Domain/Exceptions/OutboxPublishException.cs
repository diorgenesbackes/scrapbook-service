namespace ScrapBook.Domain.Exceptions;

public sealed class OutboxPublishException(bool retryable, Exception? innerException = null)
    : Exception("Falha de publicação da Outbox.", innerException)
{
    public bool Retryable { get; } = retryable;
}
