using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class EventStructuralLockRepository(ScrapBookDbContext context) : IEventStructuralLockRepository
{
    public async Task<DateTime?> FindRecordConfirmationAsync(long eventId, long recordId, CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.SqlQuery<DateTime?>($"""
            SELECT confirmed_at AS "Value"
            FROM scrapbook.records
            WHERE id = {recordId} AND records_events_id = {eventId}
            """).ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public async Task<ProcessedMessageEntity> GetOrInsertAsync(string consumer, string source, Guid messageId, byte[] payloadHash,
        Guid correlationId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scrapbook.processed_messages (
                consumer, source, message_id, payload_hash, correlation_id, state, attempt_count
            ) VALUES ({consumer}, {source}, {messageId}, {payloadHash}, {correlationId}, 'processing', 1)
            ON CONFLICT (consumer, source, message_id) WHERE message_id IS NOT NULL DO NOTHING
            """, cancellationToken));

        return (await FindForUpdateAsync(consumer, source, messageId, cancellationToken))!;
    }

    public async Task<ProcessedMessageEntity?> FindForUpdateAsync(string consumer, string source, Guid messageId,
        CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Database.SqlQuery<ProcessedMessageEntity>($"""
            SELECT id AS "Id", payload_hash AS "PayloadHash", state AS "State", error_code AS "ErrorCode"
            FROM scrapbook.processed_messages
            WHERE consumer = {consumer} AND source = {source} AND message_id = {messageId}
            FOR UPDATE
            """).ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public Task QuarantineAsync(long id, string errorCode, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.processed_messages
            SET state = 'quarantined', error_code = {errorCode}, updated_at = {now}, lease_token = NULL, lease_until = NULL
            WHERE id = {id}
            """, cancellationToken));

    public Task RearmAsync(long id, string? errorCode, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.processed_messages
            SET state = 'processing', error_code = {errorCode}, completed_at = NULL, updated_at = {now},
                attempt_count = 0, lease_token = NULL, lease_until = NULL
            WHERE id = {id}
            """, cancellationToken));

    public Task<bool> HasUnmatchedConfirmationsAsync(string type, short version, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var rows = await context.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1
                    FROM scrapbook.events AS event
                    WHERE event.locked_at IS NULL
                        AND EXISTS (
                            SELECT 1 FROM scrapbook.records AS record
                            WHERE record.records_events_id = event.id AND record.confirmed_at IS NOT NULL
                        )
                        AND NOT EXISTS (
                            SELECT 1 FROM scrapbook.outbox_messages AS fact
                            WHERE fact.type = {type} AND fact.protocol_version = {version}
                                AND fact.aggregate_type = 'event' AND fact.aggregate_id = event.id
                        )
                ) AS "Value"
                """).ToListAsync(cancellationToken);
            return rows.Single();
        });

    public Task CompleteAsync(long id, DateTimeOffset completedAt, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.processed_messages
            SET state = 'completed', completed_at = {completedAt}, updated_at = {completedAt},
                error_code = NULL, lease_token = NULL, lease_until = NULL
            WHERE id = {id}
            """, cancellationToken));

    public Task ApplyAsync(EventStructuralLockCommand command, CancellationToken cancellationToken)
    {
        var confirmedAt = new NpgsqlParameter("confirmed_at", NpgsqlDbType.Timestamp) { Value = command.ConfirmedAt };
        return ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.events
            SET locked_at = {command.LockedAt}, first_record_id = {command.RecordId}, first_record_confirmed_at = {confirmedAt},
                version = {command.Version}, updated_at = {command.LockedAt}
            WHERE id = {command.EventId}
            """, cancellationToken));
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
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
}
