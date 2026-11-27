namespace ScrapBook.Domain.Exceptions;

public sealed class MediaJobRejectedException(bool retryable, Exception? innerException = null)
    : Exception("The transcoding provider rejected the job submission.", innerException)
{
    public bool Retryable { get; } = retryable;
}
