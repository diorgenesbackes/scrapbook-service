using Microsoft.EntityFrameworkCore;
using Npgsql;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class InvitationRepository(ScrapBookDbContext context) : IInvitationRepository
{
    public Task<bool> IsAccountActiveAsync(long accountId, CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => context.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value"
            FROM scrapbook.accounts
            WHERE id = {accountId} AND state = 'active'
            """).AnyAsync(cancellationToken));
    }

    public async Task<InvitationMemberSnapshot?> FindMembersAsync(long eventId, long accountId, CancellationToken cancellationToken)
    {
        var members = await ExecuteAsync(() => context.Set<EventMemberEntity>().AsNoTracking()
            .Where(member => member.EventId == eventId)
            .Select(member => new { member.Id, member.AccountId })
            .ToListAsync(cancellationToken));
        var linked = members.SingleOrDefault(member => member.AccountId == accountId);
        return linked is null ? null : new InvitationMemberSnapshot(linked.Id, members.Count);
    }

    public Task<InvitationEntity?> FindByRequestAsync(long eventId, Guid requestKey, CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => context.Set<InvitationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(invitation => invitation.EventId == eventId && invitation.RequestKey == requestKey, cancellationToken));
    }

    public Task<InvitationEntity?> FindPendingAsync(long eventId, CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => context.Set<InvitationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(invitation => invitation.EventId == eventId && invitation.State == "pending", cancellationToken));
    }

    public async Task InsertAsync(InvitationEntity invitation, CancellationToken cancellationToken)
    {
        context.Set<InvitationEntity>().Add(invitation);
        await ExecuteAsync(() => context.SaveChangesAsync(cancellationToken));
    }

    public async Task SaveClosedAsync(InvitationEntity invitation, CancellationToken cancellationToken)
    {
        await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.invitations
            SET state = {invitation.State}, updated_at = {invitation.UpdatedAt}, invalidated_at = {invitation.InvalidatedAt},
                version = {invitation.Version}, token_ciphertext = {invitation.TokenCiphertext},
                encryption_key_id = {invitation.EncryptionKeyId}
            WHERE id = {invitation.Id}
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
        catch (DbUpdateException error) when (error.InnerException is NpgsqlException { IsTransient: true })
        {
            throw new PostgresUnavailableException(error);
        }
    }
}
