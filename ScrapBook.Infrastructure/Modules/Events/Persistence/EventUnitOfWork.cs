using Microsoft.EntityFrameworkCore;
using ScrapBook.Application.Modules.Events;

namespace ScrapBook.Infrastructure.Modules.Events.Persistence;

internal sealed class EventUnitOfWork(EventsDbContext database) : IEventUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'; SET LOCAL statement_timeout = '4s';", cancellationToken);
            var result = await action(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (EventFailure) { throw; }
        catch (Exception error) when (error is DbUpdateException or Npgsql.NpgsqlException or TimeoutException or OperationCanceledException)
        {
            // Não repetir a transação: a confirmação do commit pode ser desconhecida.
            throw EventPersistenceFailures.Translate(error);
        }
    }
}
