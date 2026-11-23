using Microsoft.EntityFrameworkCore;
using Npgsql;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class EventRepository(ScrapBookDbContext context) : IEventRepository
{
    public async Task<IReadOnlyList<EventEntity>> ListLinkedAsync(
        long accountId, long? beforeId, int limit, CancellationToken cancellationToken)
    {
        var query = Linked(accountId);
        if (beforeId is { } boundary)
        {
            query = query.Where(item => item.Id < boundary);
        }

        try
        {
            return await query.OrderByDescending(item => item.Id).Take(limit).ToListAsync(cancellationToken);
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }

    public async Task<EventEntity?> FindLinkedAsync(long accountId, long eventId, CancellationToken cancellationToken)
    {
        try
        {
            return await Linked(accountId).SingleOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }

    public async Task<EventEntity?> FindCreationAsync(long accountId, byte[] keyHash, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Set<EventEntity>().AsNoTracking().SingleOrDefaultAsync(
                item => item.CreatorAccountId == accountId && item.CreationKeyHash == keyHash, cancellationToken);
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            throw new PostgresUnavailableException(error);
        }
    }

    public async Task<PublicEventModel?> FindByCodeAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Set<EventEntity>().AsNoTracking()
                .Where(item => item.Code == code)
                .Select(item => new PublicEventModel(item.Name, item.WelcomeMessage, item.StartsAt,
                    item.SubmissionOpensAt, item.SubmissionClosesAt, item.CompletionDeadlineAt))
                .SingleOrDefaultAsync(cancellationToken);
        }
        catch (NpgsqlException error) when (error.IsTransient
            || error is PostgresException { SqlState: PostgresErrorCodes.TooManyConnections })
        {
            throw new PostgresUnavailableException(error);
        }
    }

    public async Task InsertAsync(EventEntity item, EventMemberEntity member, CancellationToken cancellationToken)
    {
        context.Add(item);
        context.Add(member);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            context.ChangeTracker.Clear();
            throw postgres.ConstraintName switch
            {
                "uq_events_creation_key" => new EventCreationKeyCollisionException(error),
                "uq_events_code" => new EventCodeCollisionException(error),
                _ => error
            };
        }
        catch (DbUpdateException error) when (error.InnerException is NpgsqlException { IsTransient: true })
        {
            throw new PostgresUnavailableException(error);
        }
    }

    private IQueryable<EventEntity> Linked(long accountId) =>
        from item in context.Set<EventEntity>().AsNoTracking()
        join member in context.Set<EventMemberEntity>() on item.Id equals member.EventId
        where member.AccountId == accountId
        select item;
}
