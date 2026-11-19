namespace ScrapBook.Domain.Exceptions;

public sealed class PostgresUnavailableException : Exception
{
    public PostgresUnavailableException() : base("PostgreSQL indisponível.")
    {
    }
}
