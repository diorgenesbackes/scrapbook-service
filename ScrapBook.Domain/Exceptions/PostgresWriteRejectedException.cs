namespace ScrapBook.Domain.Exceptions;

public sealed class PostgresWriteRejectedException(Exception? innerException = null)
    : Exception("Escrita PostgreSQL rejeitada.", innerException);
