namespace ScrapBook.Domain.Exceptions;

public sealed class MediaVariantStorageException(Exception? innerException = null)
    : Exception("Private media variant storage operation failed.", innerException);
