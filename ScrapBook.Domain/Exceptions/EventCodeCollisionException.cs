namespace ScrapBook.Domain.Exceptions;

public sealed class EventCodeCollisionException(Exception innerException) : Exception(null, innerException);
