namespace ScrapBook.Domain.Exceptions;

public sealed class PublicEventTimeoutException(Exception innerException) : Exception(null, innerException);
