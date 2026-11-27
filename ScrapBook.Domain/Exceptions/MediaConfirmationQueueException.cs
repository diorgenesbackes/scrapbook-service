namespace ScrapBook.Domain.Exceptions;

public sealed class MediaConfirmationQueueException(Exception innerException)
    : Exception("The media confirmation queue operation failed.", innerException);
