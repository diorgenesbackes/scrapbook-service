namespace ScrapBook.Domain.Exceptions;

public sealed class PhotoStorageUnavailableException(Exception? innerException = null)
    : Exception("Photo storage unavailable.", innerException);
