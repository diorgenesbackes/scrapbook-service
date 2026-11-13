using ScrapBook.Application.Builders;
using ScrapBook.Application.Enums;
using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Models;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.DbContexts;
using ScrapBook.Infrastructure.Entities;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ScrapBook.Infrastructure.Repositories;

internal sealed class EventRepository(ScrapBookDbContext database, EventTelemetryService telemetry) : IEventRepository
{
    public async Task<EventEntity?> GetAsync(long accountId, long eventId, CancellationToken cancellationToken)
    {
        using var dependency = telemetry.Dependency("postgresql", "events.sql.get");
        var row = await ForActor(accountId).AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        dependency.Complete();
        return row?.Snapshot();
    }

    public async Task<IReadOnlyList<EventListItemModel>> ListAsync(long accountId, int count, EventCursorModel? cursor, CancellationToken cancellationToken)
    {
        using var dependency = telemetry.Dependency("postgresql", "events.sql.list");
        var query = ForActor(accountId).AsNoTracking();
        if (cursor is not null) query = query.Where(e => e.CreatedAt < cursor.CreatedAt
            || (e.CreatedAt == cursor.CreatedAt && e.Id < cursor.EventId));
        var records = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(count)
            .Select(e => new EventListItemModel(e.Id, e.Name, e.StartsAt, e.OpeningMode, e.OpensAt, e.Version, e.CreatedAt))
            .ToListAsync(cancellationToken);
        dependency.Complete();
        return records;
    }

    public async Task<EventEntity> CreateAsync(EventEntity state, DateTimeOffset joinedAt, Guid correlationId, CancellationToken cancellationToken)
    {
        using var dependency = telemetry.Dependency("postgresql", "events.sql.create");
        var row = EventPersistenceEntity.FromSnapshot(state);
        database.Events.Add(row);
        await database.SaveChangesAsync(cancellationToken);
        database.Members.Add(new EventMemberPersistenceEntity { EventId = row.Id, AccountId = state.CreatorId, Slot = 1, JoinedAt = joinedAt });
        database.Audit.Add(AuditRow(EventAuditBuilder.Created(state.CreatorId, row.Snapshot(), correlationId)));
        await database.SaveChangesAsync(cancellationToken);
        await database.Entry(row).ReloadAsync(cancellationToken);
        dependency.Complete();
        return row.Snapshot();
    }

    public async Task<EventEntity> MutateAsync(long accountId, long eventId, long expectedVersion, EventMutationEnum mutation,
        Func<EventEntity, EventChangeModel> apply, Guid correlationId, CancellationToken cancellationToken)
    {
        using var dependency = telemetry.Dependency("postgresql", "events.sql.mutate");
        // Lock inclui o vínculo no SQL e não lê colunas privadas de primeiro registro.
        var lockedId = await database.Database.SqlQuery<long>($"""
            SELECT e.id AS "Value" FROM events.events e
            WHERE e.id = {eventId} AND EXISTS (SELECT 1 FROM events.event_members m
                WHERE m.event_members_events_id=e.id AND m.event_members_accounts_id={accountId})
            FOR NO KEY UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (lockedId == 0) throw new EventApplicationException(EventFailureKindEnum.NotFound);
        var row = await ForActor(accountId).SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken)
            ?? throw new EventApplicationException(EventFailureKindEnum.NotFound);
        if (row.Version != expectedVersion) throw new EventApplicationException(EventFailureKindEnum.VersionConflict);
        var before = row.Snapshot();
        var change = apply(before); // Clock e travas reavaliados após adquirir o lock.
        if (!change.Changed) { dependency.Complete(); return before; }
        row.Apply(change.State);
        // IsConcurrencyToken inclui a versão original no predicado UPDATE.
        await database.SaveChangesAsync(cancellationToken);
        await database.Entry(row).ReloadAsync(cancellationToken);
        var after = row.Snapshot();
        if (change.AccessChanged)
        {
            await database.Dispatches.Where(d => d.EventId == eventId && d.AccessVersion < after.AccessVersion
                && (d.State == "pending" || d.State == "requested"))
                .ExecuteUpdateAsync(update => update.SetProperty(d => d.State, "cancelled"), cancellationToken);
        }
        database.Audit.Add(AuditRow(EventAuditBuilder.Updated(accountId, before, after, mutation, correlationId)));
        await database.SaveChangesAsync(cancellationToken);
        dependency.Complete();
        return after;
    }

    private IQueryable<EventPersistenceEntity> ForActor(long accountId) => database.Events.Where(e =>
        database.Members.Any(m => m.EventId == e.Id && m.AccountId == accountId));

    private static EventAuditPersistenceEntity AuditRow(EventAuditModel audit) => new()
    {
        AccountId = audit.AccountId,
        EventId = audit.EventId,
        ActorType = "account",
        Action = audit.Action,
        SubjectType = "event",
        SubjectId = audit.EventId,
        Outcome = "success",
        ReasonCode = audit.ReasonCode,
        CorrelationId = audit.CorrelationId,
        BeforeState = JsonSerializer.Serialize(audit.Before),
        AfterState = JsonSerializer.Serialize(audit.After)
    };
}
