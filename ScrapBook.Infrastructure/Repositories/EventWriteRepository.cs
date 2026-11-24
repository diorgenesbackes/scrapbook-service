using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class EventWriteRepository(ScrapBookDbContext context) : IEventWriteRepository
{
    public async Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new EventTransaction(await context.Database.BeginTransactionAsync(cancellationToken));
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }

    public Task<EventEntity?> FindLinkedForUpdateAsync(long accountId, long eventId, CancellationToken cancellationToken)
    {
        return LoadLockedEventAsync($"""
            SELECT event.*
            FROM scrapbook.events AS event
            WHERE event.id = {eventId} AND EXISTS (
                SELECT 1 FROM scrapbook.event_members AS member
                WHERE member.event_members_events_id = event.id AND member.event_members_accounts_id = {accountId}
            )
            FOR UPDATE OF event
            """, cancellationToken);
    }

    public Task<EventEntity?> FindForUpdateAsync(long eventId, CancellationToken cancellationToken)
    {
        return LoadLockedEventAsync($"""
            SELECT event.* FROM scrapbook.events AS event WHERE event.id = {eventId} FOR UPDATE OF event
            """, cancellationToken);
    }

    private async Task<EventEntity?> LoadLockedEventAsync(FormattableString sql, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await context.Set<EventEntity>().FromSqlInterpolated(sql).AsNoTracking().ToListAsync(cancellationToken);
            return rows.SingleOrDefault();
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }

    public async Task SaveDetailsAsync(EventEntity item, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        var starts = Timestamp("starts_at", item.StartsAt);
        var opens = Timestamp("submission_opens_at", item.SubmissionOpensAt);
        var closes = Timestamp("submission_closes_at", item.SubmissionClosesAt);
        var deadline = Timestamp("completion_deadline_at", item.CompletionDeadlineAt);
        var frozen = Timestamp("frozen_at", item.AccessFrozenAt);
        await ExecuteAsync($"""
            UPDATE scrapbook.events
            SET name = {item.Name},
                welcome_message = {item.WelcomeMessage},
                starts_at = {starts},
                submission_opens_at = {opens},
                submission_closes_at = {closes},
                completion_deadline_at = {deadline},
                access_state = {item.AccessState},
                access_frozen_at = {frozen},
                version = {item.Version},
                updated_at = {updatedAt}
            WHERE id = {item.Id}
            """, cancellationToken);
    }

    public async Task SaveOpeningAsync(EventEntity item, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        var opens = Timestamp("opens_at", item.OpensAt);
        var frozen = Timestamp("frozen_at", item.AccessFrozenAt);
        await ExecuteAsync($"""
            UPDATE scrapbook.events
            SET opening_mode = {item.OpeningMode},
                opens_at = {opens},
                access_state = {item.AccessState},
                access_frozen_at = {frozen},
                access_version = {item.AccessVersion},
                version = {item.Version},
                updated_at = {updatedAt}
            WHERE id = {item.Id}
            """, cancellationToken);
    }

    public async Task InsertAuditAsync(AuditEntryEntity audit, CancellationToken cancellationToken)
    {
        await ExecuteAsync($"""
            INSERT INTO scrapbook.audit_entries (
                audit_entries_accounts_id, audit_entries_events_id, actor_type, action, subject_type,
                subject_id, outcome, correlation_id, created_at, expires_at, before_state, after_state, area
            ) VALUES (
                {audit.AccountId}, {audit.EventId}, {audit.ActorType}, {audit.Action}, {audit.SubjectType},
                {audit.SubjectId ?? audit.EventId}, {audit.Outcome}, {audit.CorrelationId}, {audit.CreatedAt}, {audit.ExpiresAt},
                CAST({audit.BeforeState} AS jsonb), CAST({audit.AfterState} AS jsonb), {audit.Area}
            )
            """, cancellationToken);
    }

    private static NpgsqlParameter Timestamp(string name, DateTime? value) =>
        new(name, NpgsqlDbType.Timestamp) { Value = (object?)value ?? DBNull.Value };

    private async Task ExecuteAsync(FormattableString sql, CancellationToken cancellationToken)
    {
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync(sql, cancellationToken);
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }
}
