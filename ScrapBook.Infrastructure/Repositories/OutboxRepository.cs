using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class OutboxRepository(ScrapBookDbContext context) : IOutboxRepository
{
    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken) =>
        ExecuteAsync<IEventTransaction>(async () => new EventTransaction(await context.Database.BeginTransactionAsync(cancellationToken)));

    public Task<OutboxMessageEntity?> FindAsync(Guid messageId, CancellationToken cancellationToken) =>
        FindMessageAsync(messageId, false, cancellationToken);

    public Task<OutboxMessageEntity?> FindForUpdateAsync(Guid messageId, CancellationToken cancellationToken) =>
        FindMessageAsync(messageId, true, cancellationToken);

    private async Task<OutboxMessageEntity?> FindMessageAsync(Guid messageId, bool forUpdate, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT message_id AS "MessageId", type AS "Type", protocol_version AS "ProtocolVersion",
                aggregate_type AS "AggregateType", aggregate_id AS "EventId", payload::text AS "Payload",
                correlation_id AS "CorrelationId", occurred_at AS "OccurredAt", attempt_count AS "AttemptCount",
                lease_token AS "LeaseToken", state AS "State"
            FROM scrapbook.outbox_messages
            WHERE message_id = {0}
            """;
        var sql = FormattableStringFactory.Create(forUpdate ? query + " FOR UPDATE" : query, messageId);
        var rows = await ExecuteAsync(() => context.Database.SqlQuery<OutboxMessageEntity>(sql).ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public Task InsertResultAsync(StructuralLockOutboxEntity result, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scrapbook.outbox_messages (
                message_id, type, protocol_version, aggregate_type, aggregate_id, payload,
                correlation_id, causation_id, occurred_at, state, available_at
            ) VALUES (
                {result.MessageId}, {result.Type}, 1, 'event', {result.EventId}, CAST({result.Payload} AS jsonb),
                {result.CorrelationId}, {result.CausationId}, {result.OccurredAt}, 'pending', {result.OccurredAt}
            )
            ON CONFLICT (message_id) DO NOTHING
            """, cancellationToken));

    public Task ReopenAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.outbox_messages
            SET state = 'pending', attempt_count = 0, lease_token = NULL, lease_until = NULL,
                last_error_code = NULL, available_at = {now}, updated_at = {now}
            WHERE message_id = {messageId}
            """, cancellationToken));

    public Task<OutboxBacklogModel> GetBacklogAsync(string type, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var rows = await context.Database.SqlQuery<OutboxBacklogModel>($"""
                SELECT count(*) FILTER (WHERE state IN ('pending', 'publishing')) AS "PendingCount",
                    count(*) FILTER (WHERE state = 'failed') AS "FailedCount",
                    min(occurred_at) FILTER (WHERE state IN ('pending', 'publishing')) AS "OldestPendingAt"
                FROM scrapbook.outbox_messages
                WHERE type = {type} AND state IN ('pending', 'publishing', 'failed')
                """).ToListAsync(cancellationToken);
            return rows.Single();
        });

    public Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(string type, int limit, Guid leaseToken,
        DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<OutboxMessageEntity>>(async () => await context.Database.SqlQuery<OutboxMessageEntity>($"""
            WITH eligible AS (
                SELECT id
                FROM scrapbook.outbox_messages
                WHERE type = {type}
                    AND state IN ('pending', 'publishing')
                    AND available_at <= {now}
                    AND (lease_until IS NULL OR lease_until <= {now})
                ORDER BY available_at, id
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
            )
            UPDATE scrapbook.outbox_messages AS message
            SET state = 'publishing', lease_token = {leaseToken}, lease_until = {leaseUntil},
                available_at = {leaseUntil}, attempt_count = attempt_count + 1, updated_at = {now}
            FROM eligible
            WHERE message.id = eligible.id
            RETURNING message_id AS "MessageId", type AS "Type", protocol_version AS "ProtocolVersion",
                aggregate_type AS "AggregateType", aggregate_id AS "EventId", payload::text AS "Payload",
                correlation_id AS "CorrelationId", occurred_at AS "OccurredAt", attempt_count AS "AttemptCount",
                lease_token AS "LeaseToken", state AS "State"
            """).ToListAsync(cancellationToken));

    public Task<bool> MarkPublishedAsync(Guid messageId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) =>
        UpdateAsync($"""
            UPDATE scrapbook.outbox_messages
            SET state = 'published', published_at = {now}, updated_at = {now},
                last_error_code = NULL, lease_token = NULL, lease_until = NULL
            WHERE message_id = {messageId} AND state = 'publishing' AND lease_token = {leaseToken}
                AND lease_until > GREATEST({now}, clock_timestamp())
            """, cancellationToken);

    public Task<bool> MarkFailedAsync(Guid messageId, Guid leaseToken, bool terminal, string errorCode, DateTimeOffset now,
        DateTimeOffset availableAt, CancellationToken cancellationToken) =>
        UpdateAsync($"""
            UPDATE scrapbook.outbox_messages
            SET state = {(terminal ? "failed" : "pending")}, last_error_code = {errorCode}, lease_token = NULL,
                lease_until = NULL, available_at = {availableAt}, updated_at = {now}
            WHERE message_id = {messageId} AND state = 'publishing' AND lease_token = {leaseToken}
                AND lease_until > GREATEST({now}, clock_timestamp())
            """, cancellationToken);

    private Task<bool> UpdateAsync(FormattableString sql, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => await context.Database.ExecuteSqlInterpolatedAsync(sql, cancellationToken) == 1);

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
