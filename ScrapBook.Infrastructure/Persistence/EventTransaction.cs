using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Infrastructure.Persistence;

public sealed class EventTransaction(IDbContextTransaction transaction) : IEventTransaction
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
        catch (PostgresException error) when (error.SqlState.StartsWith("23", StringComparison.Ordinal)
            || error.SqlState == PostgresErrorCodes.RaiseException)
        {
            throw new PostgresWriteRejectedException(error);
        }
    }

    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
