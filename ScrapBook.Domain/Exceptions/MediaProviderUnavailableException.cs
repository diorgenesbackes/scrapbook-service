namespace ScrapBook.Domain.Exceptions;

public sealed class MediaProviderUnavailableException(Exception? innerException = null)
    : Exception("The transcoding provider result could not be established.", innerException);
