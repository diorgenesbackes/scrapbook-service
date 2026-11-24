using System.Globalization;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class InvitationAcceptanceService(
    IEventWriteRepository events, IInvitationRepository invitations, IInvitationTokenCodec tokens, TimeProvider clock)
{
    public async Task<InvitationPreviewResponse> PreviewAsync(long accountId, string token, CancellationToken cancellationToken)
    {
        await using var transaction = await events.BeginAsync(cancellationToken);
        var (item, invitation) = await FindInvitationAsync(accountId, token, cancellationToken);
        var replay = await ValidateAsync(item, invitation, accountId, clock.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return replay
            ? new InvitationPreviewResponse("accepted", EventId: Identifier(item.Id))
            : new InvitationPreviewResponse("eligible", item.Name, invitation.ExpiresAt);
    }

    public async Task<InvitationAcceptanceResponse> AcceptAsync(
        long accountId, string token, Guid correlation, CancellationToken cancellationToken)
    {
        await using var transaction = await events.BeginAsync(cancellationToken);
        var (item, invitation) = await FindInvitationAsync(accountId, token, cancellationToken);
        var now = clock.GetUtcNow();
        if (!await ValidateAsync(item, invitation, accountId, now, cancellationToken))
        {
            await ApplyAcceptanceAsync(invitation, accountId, correlation,
                now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond)), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new InvitationAcceptanceResponse("accepted", Identifier(item.Id));
    }

    private async Task<(EventEntity Event, InvitationEntity Invitation)> FindInvitationAsync(
        long accountId, string token, CancellationToken cancellationToken)
    {
        var account = await invitations.FindAccountForShareAsync(accountId, cancellationToken);
        if (account is not { State: "active" })
        {
            throw new EventIdentityRequiredException();
        }
        if (!account.EmailVerified)
        {
            throw new InvitationAccountUnconfirmedException();
        }
        if (!tokens.IsAvailable)
        {
            throw new InvitationProtectionUnavailableException();
        }

        var digest = tokens.Digest(token) ?? throw new InvitationNotFoundException();
        var locator = await invitations.FindByDigestAsync(digest, cancellationToken) ?? throw new InvitationNotFoundException();
        var item = await events.FindForUpdateAsync(locator.EventId, cancellationToken) ?? throw new InvitationNotFoundException();
        var invitation = await invitations.FindForUpdateAsync(locator, cancellationToken) ?? throw new InvitationNotFoundException();
        return (item, invitation);
    }

    private async Task<bool> ValidateAsync(
        EventEntity item, InvitationEntity invitation, long accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invitation.State == "accepted")
        {
            if (invitation.RecipientAccountId == accountId
                && (await invitations.FindMembersAsync(item.Id, accountId, cancellationToken)).MemberId.HasValue)
            {
                return true;
            }

            throw new InvitationAlreadyUsedException();
        }
        if (item.LockedAt.HasValue)
        {
            throw new InvitationEventLockedException();
        }
        if (invitation.State != "pending" || now < invitation.CreatedAt || now >= invitation.ExpiresAt)
        {
            throw new InvitationUnavailableException();
        }

        var members = await invitations.FindMembersAsync(item.Id, accountId, cancellationToken);
        if (members.Count != 1)
        {
            throw new InvitationEventFullException();
        }
        if (members.MemberId.HasValue)
        {
            throw new InvitationAccountAlreadyLinkedException();
        }

        return false;
    }

    private async Task ApplyAcceptanceAsync(
        InvitationEntity invitation, long accountId, Guid correlation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await invitations.InsertMemberAsync(new EventMemberEntity
        {
            EventId = invitation.EventId,
            AccountId = accountId,
            Slot = 2,
            JoinedAt = now
        }, cancellationToken);

        invitation.State = "accepted";
        invitation.RecipientAccountId = accountId;
        invitation.AcceptedAt = now;
        invitation.UpdatedAt = now;
        invitation.Version++;
        invitation.EncryptionKeyId = null;
        invitation.TokenCiphertext = null;
        await invitations.SaveClosedAsync(invitation, cancellationToken);
        await events.InsertAuditAsync(new AuditEntryEntity(
            accountId, invitation.EventId, "account", "invitation.accepted", "invitation", "success", correlation,
            now, now.AddDays(180), """{"state":"pending"}""", """{"state":"accepted","slot":2}""",
            "events", invitation.Id), cancellationToken);
    }

    private static string Identifier(long id) => id.ToString(CultureInfo.InvariantCulture);
}
