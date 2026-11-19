namespace ScrapBook.Domain.Exceptions;

public sealed class PostgresUnavailableException(Exception? innerException = null)
    : Exception("PostgreSQL indisponível.", innerException);
