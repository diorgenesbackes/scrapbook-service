namespace ScrapBook.Domain.Exceptions;

public sealed class EventCreationKeyCollisionException(Exception innerException) : Exception(null, innerException);
