namespace ScrapBook.Domain.Exceptions;

public sealed class MediaAudioResultQueueException(Exception innerException)
    : Exception("The media audio result queue operation failed.", innerException);
