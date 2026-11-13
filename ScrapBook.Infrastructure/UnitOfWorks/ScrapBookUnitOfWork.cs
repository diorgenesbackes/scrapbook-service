using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Services;
using ScrapBook.Infrastructure.DbContexts;
using ScrapBook.Infrastructure.Mappers;
using Microsoft.EntityFrameworkCore;

namespace ScrapBook.Infrastructure.UnitOfWorks;

internal sealed class ScrapBookUnitOfWork(ScrapBookDbContext database, EventTelemetryService telemetry) : IUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var dependency = telemetry.Dependency("postgresql", "events.sql.transaction");
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'; SET LOCAL statement_timeout = '4s';", cancellationToken);
            var result = await action(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            dependency.Complete();
            return result;
        }
        catch (EventApplicationException) { throw; }
        catch (Exception error) when (error is DbUpdateException or Npgsql.NpgsqlException or TimeoutException or OperationCanceledException)
        {
            // Não repetir a transação: a confirmação do commit pode ser desconhecida.
            throw EfPostgresExceptionMapper.Translate(error);
        }
    }
}
