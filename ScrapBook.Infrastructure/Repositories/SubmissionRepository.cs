using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class SubmissionRepository(ScrapBookDbContext context) : ISubmissionRepository
{
    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken) =>
        ReadAsync<IEventTransaction>(async () => new EventTransaction(await context.Database.BeginTransactionAsync(cancellationToken)));

    public async Task<EventEntity?> FindEventAsync(string code, bool forUpdate, CancellationToken cancellationToken)
    {
        FormattableString sql = forUpdate
            ? (FormattableString)$"SELECT event.* FROM scrapbook.events AS event WHERE event.code = {code} FOR UPDATE OF event"
            : $"SELECT event.* FROM scrapbook.events AS event WHERE event.code = {code}";
        var rows = await ReadAsync(() => context.Set<EventEntity>().FromSqlInterpolated(sql).AsNoTracking()
            .ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public async Task<SubmissionNoticeEntity?> FindNoticeAsync(DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(() => context.Database.SqlQuery<SubmissionNoticeEntity>($"""
            SELECT id AS "Id", code AS "Code", body AS "Body"
            FROM scrapbook.consent_notices
            WHERE effective_at <= {effectiveAt}
            ORDER BY effective_at DESC, id DESC
            LIMIT 1
            """).ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public async Task<SubmissionAttemptEntity?> FindAttemptAsync(long eventId, string operation, Guid attemptKey,
        CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(() => context.Database.SqlQuery<SubmissionAttemptEntity>($"""
            SELECT record.id AS "RecordId", record.format AS "Format", operation.result_code AS "ResultCode",
                session.completion_deadline_at AS "CompletionDeadlineAt",
                CASE WHEN operation.state = 'in_progress' THEN session.state ELSE operation.state END AS "State",
                record.confirmed_at AS "ConfirmedAt",
                operation.hash_key_id AS "HashKeyId", operation.payload_hash AS "PayloadHash",
                operation.authorization_hash AS "AuthorizationHash",
                operation.authorization_expires_at AS "AuthorizationExpiresAt"
            FROM scrapbook.idempotent_operations AS operation
            JOIN scrapbook.records AS record
                ON record.id = operation.idempotent_operations_records_id
                AND record.records_events_id = operation.idempotent_operations_events_id
            JOIN scrapbook.upload_sessions AS session ON session.upload_sessions_idempotent_operations_id = operation.id
            WHERE operation.idempotent_operations_events_id = {eventId}
                AND operation.operation = {operation} AND operation.key = {attemptKey}
            """).ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public async Task<bool> HasConfirmedAsync(long eventId, Guid firstMessageId, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(() => context.Database.SqlQuery<bool>($"""
            SELECT (
                EXISTS (SELECT 1 FROM scrapbook.records WHERE records_events_id = {eventId} AND confirmed_at IS NOT NULL)
                OR EXISTS (SELECT 1 FROM scrapbook.outbox_messages WHERE message_id = {firstMessageId})
            ) AS "Value"
            """).ToListAsync(cancellationToken));
        return rows.Single();
    }

    public async Task<long> InsertConfirmedTextAsync(ConfirmTextSubmissionCommand command, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(() => context.Database.SqlQuery<long>($"""
            WITH inserted_record AS (
                INSERT INTO scrapbook.records (
                    records_events_id, records_consent_notices_id, category, format, sender_name, message,
                    expected_file_count, consented_at, submitted_at, received_at, confirmed_at, state
                ) VALUES (
                    {command.EventId}, {command.NoticeId}, 'testimonial', 'text', {command.GuestName}, {command.Text},
                    0, {CivilTimestamp("consented_at", command.ConsentedAt)}, {CivilTimestamp("submitted_at", command.SubmittedAt)},
                    {CivilTimestamp("received_at", command.ReceivedAt)}, {CivilTimestamp("confirmed_at", command.ConfirmedAt)}, 'confirmed'
                ) RETURNING id
            ), inserted_operation AS (
                INSERT INTO scrapbook.idempotent_operations (
                    idempotent_operations_events_id, idempotent_operations_records_id, operation, key, payload_hash,
                    authorization_hash, hash_key_id, authorization_expires_at, state, completed_at
                )
                SELECT {command.EventId}, id, {command.Operation}, {command.AttemptKey}, {command.PayloadHash},
                    {command.AuthorizationHash}, {command.HashKeyId}, {command.AuthorizationExpiresAt}, 'confirmed', {command.CommittedAt}
                FROM inserted_record
                RETURNING id, idempotent_operations_records_id
            ), inserted_session AS (
                INSERT INTO scrapbook.upload_sessions (
                    upload_sessions_events_id, upload_sessions_records_id, upload_sessions_idempotent_operations_id,
                    state, window_opens_at, window_closes_at, completion_deadline_at, started_at, sealed_at, terminal_at
                )
                SELECT {command.EventId}, idempotent_operations_records_id, id, 'confirmed',
                    {CivilTimestamp("window_opens_at", command.WindowOpensAt)}, {CivilTimestamp("window_closes_at", command.WindowClosesAt)},
                    {CivilTimestamp("completion_deadline_at", command.CompletionDeadlineAt)},
                    {CivilTimestamp("started_at", command.SubmittedAt)},
                    {command.CommittedAt}, {command.CommittedAt}
                FROM inserted_operation
                RETURNING upload_sessions_records_id
            )
            SELECT upload_sessions_records_id AS "Value" FROM inserted_session
            """).ToListAsync(cancellationToken));
        return rows.Single();
    }

    private static NpgsqlParameter CivilTimestamp(string name, DateTime value) =>
        new(name, NpgsqlDbType.Timestamp) { Value = value };

    public async Task InsertOutboxAsync(SubmissionOutboxEntity message, CancellationToken cancellationToken)
    {
        await ReadAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scrapbook.outbox_messages (
                message_id, type, protocol_version, aggregate_type, aggregate_id, payload,
                correlation_id, occurred_at, state, available_at
            ) VALUES (
                {message.MessageId}, {message.Type}, 1, 'event', {message.EventId}, CAST({message.Payload} AS jsonb),
                {message.CorrelationId}, {message.OccurredAt}, 'pending', {message.OccurredAt}
            )
            """, cancellationToken));
    }

    private static async Task<T> ReadAsync<T>(Func<Task<T>> execute)
    {
        try
        {
            return await execute();
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }
}
