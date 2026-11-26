using Microsoft.EntityFrameworkCore;
using Npgsql;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class OutboxRepository(ScrapBookDbContext context) : IOutboxRepository
{
    public async Task<OutboxMessageEntity?> FindAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync($"""
            SELECT message_id AS "MessageId", type AS "Type", protocol_version AS "ProtocolVersion",
                aggregate_type AS "AggregateType", aggregate_id AS "EventId", payload::text AS "Payload",
                correlation_id AS "CorrelationId", occurred_at AS "OccurredAt", attempt_count AS "AttemptCount",
                lease_token AS "LeaseToken"
            FROM scrapbook.outbox_messages
            WHERE message_id = {messageId}
            """, cancellationToken);
        return rows.SingleOrDefault();
    }

    public Task<IReadOnlyList<OutboxMessageEntity>> ClaimAsync(string type, int limit, Guid leaseToken,
        DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken) =>
        QueryAsync($"""
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
                lease_token AS "LeaseToken"
            """, cancellationToken);

    public Task<bool> MarkPublishedAsync(Guid messageId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) =>
        UpdateAsync($"""
            UPDATE scrapbook.outbox_messages
            SET state = 'published', published_at = {now}, updated_at = {now},
                last_error_code = NULL, lease_token = NULL, lease_until = NULL
            WHERE message_id = {messageId} AND state = 'publishing' AND lease_token = {leaseToken} AND lease_until > {now}
            """, cancellationToken);

    public Task<bool> MarkFailedAsync(Guid messageId, Guid leaseToken, bool terminal, string errorCode, DateTimeOffset now,
        DateTimeOffset availableAt, CancellationToken cancellationToken) =>
        UpdateAsync($"""
            UPDATE scrapbook.outbox_messages
            SET state = {(terminal ? "failed" : "pending")}, last_error_code = {errorCode}, lease_token = NULL,
                lease_until = NULL, available_at = {availableAt}, updated_at = {now}
            WHERE message_id = {messageId} AND state = 'publishing' AND lease_token = {leaseToken} AND lease_until > {now}
            """, cancellationToken);

    private async Task<IReadOnlyList<OutboxMessageEntity>> QueryAsync(FormattableString sql, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Database.SqlQuery<OutboxMessageEntity>(sql).ToListAsync(cancellationToken);
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }

    private async Task<bool> UpdateAsync(FormattableString sql, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Database.ExecuteSqlInterpolatedAsync(sql, cancellationToken) == 1;
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }
}
