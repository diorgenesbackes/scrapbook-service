namespace ScrapBook.Domain.Exceptions;

public sealed class PostgresReadinessTimeoutException(Exception innerException)
    : Exception("Tempo limite da sondagem PostgreSQL excedido.", innerException);
