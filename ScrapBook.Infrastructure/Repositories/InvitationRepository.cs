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

    public async Task<InvitationAccountSnapshot?> FindAccountForShareAsync(long accountId, CancellationToken cancellationToken)
    {
        var accounts = await ExecuteAsync(() => context.Database.SqlQuery<InvitationAccountSnapshot>($"""
            SELECT state AS "State", email_verified AS "EmailVerified"
            FROM scrapbook.accounts
            WHERE id = {accountId}
            FOR SHARE
            """).ToListAsync(cancellationToken));
        return accounts.SingleOrDefault();
    }

    public async Task<InvitationMemberSnapshot> FindMembersAsync(long eventId, long accountId, CancellationToken cancellationToken)
    {
        var members = await ExecuteAsync(() => context.Set<EventMemberEntity>().AsNoTracking()
            .Where(member => member.EventId == eventId)
            .Select(member => new { member.Id, member.AccountId })
            .ToListAsync(cancellationToken));
        var linked = members.SingleOrDefault(member => member.AccountId == accountId);
        return new InvitationMemberSnapshot(linked?.Id, members.Count);
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

    public Task<InvitationLocator?> FindByDigestAsync(InvitationDigest digest, CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => context.Set<InvitationEntity>().AsNoTracking()
            .Where(invitation => invitation.HashKeyId == digest.KeyId && invitation.TokenHash == digest.Hash)
            .Select(invitation => new InvitationLocator(invitation.Id, invitation.EventId))
            .SingleOrDefaultAsync(cancellationToken));
    }

    public async Task<InvitationEntity?> FindForUpdateAsync(InvitationLocator invitation, CancellationToken cancellationToken)
    {
        var rows = await ExecuteAsync(() => context.Set<InvitationEntity>().FromSqlInterpolated($"""
            SELECT *
            FROM scrapbook.invitations
            WHERE id = {invitation.Id} AND invitations_events_id = {invitation.EventId}
            FOR UPDATE
            """).AsNoTracking().ToListAsync(cancellationToken));
        return rows.SingleOrDefault();
    }

    public async Task InsertAsync(InvitationEntity invitation, CancellationToken cancellationToken)
    {
        context.Set<InvitationEntity>().Add(invitation);
        await ExecuteAsync(() => context.SaveChangesAsync(cancellationToken));
    }

    public async Task InsertMemberAsync(EventMemberEntity member, CancellationToken cancellationToken)
    {
        context.Set<EventMemberEntity>().Add(member);
        await ExecuteAsync(() => context.SaveChangesAsync(cancellationToken));
    }

    public async Task SaveClosedAsync(InvitationEntity invitation, CancellationToken cancellationToken)
    {
        await ExecuteAsync(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE scrapbook.invitations
            SET state = {invitation.State}, updated_at = {invitation.UpdatedAt}, invalidated_at = {invitation.InvalidatedAt},
                version = {invitation.Version}, token_ciphertext = {invitation.TokenCiphertext},
                encryption_key_id = {invitation.EncryptionKeyId}, accepted_at = {invitation.AcceptedAt},
                invitations_recipient_accounts_id = {invitation.RecipientAccountId}
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
        catch (PostgresException error) when (error.SqlState.StartsWith("23", StringComparison.Ordinal)
            || error.SqlState == PostgresErrorCodes.RaiseException)
        {
            throw new PostgresWriteRejectedException(error);
        }
    }
}
