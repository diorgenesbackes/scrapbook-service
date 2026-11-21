using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Infrastructure.Persistence;

public sealed class EventOpeningTransaction(IDbContextTransaction transaction) : IEventOpeningTransaction
{
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }

    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
