namespace ScrapBook.Domain.Exceptions;

public sealed class MediaVariantStorageException(Exception? innerException = null)
    : Exception("Private photo variant storage operation failed.", innerException);
