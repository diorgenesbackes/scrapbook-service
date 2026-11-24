using System.Globalization;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class InvitationService(
    IEventWriteRepository events, IInvitationRepository invitations, IInvitationTokenCodec tokens, TimeProvider clock)
{
    public async Task<InvitationStatusResponse> GetCurrentAsync(
        long accountId, string eventId, CancellationToken cancellationToken)
    {
        var id = EventService.ParseEventId(eventId);
        await using var transaction = await events.BeginAsync(cancellationToken);
        var (item, member) = await GetLinkedAsync(accountId, id, cancellationToken);
        var result = await ReadCurrentAsync(item, member, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<CreateInvitationResponse> CreateAsync(
        long accountId, string eventId, string? key, Guid correlation, CancellationToken cancellationToken)
    {
        var id = EventService.ParseEventId(eventId);
        var requestKey = EventService.ParseRequestKey(key);
        await using var transaction = await events.BeginAsync(cancellationToken);
        var (item, member) = await GetLinkedAsync(accountId, id, cancellationToken);
        var replay = await invitations.FindByRequestAsync(id, requestKey, cancellationToken);
        var now = clock.GetUtcNow();
        var result = replay is null
            ? await IssueAsync(item, member, accountId, requestKey, correlation, now, cancellationToken)
            : Replay(item, member, replay, now);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<(EventEntity Item, InvitationMemberSnapshot Member)> GetLinkedAsync(
        long accountId, long eventId, CancellationToken cancellationToken)
    {
        if (!await invitations.IsAccountActiveAsync(accountId, cancellationToken))
        {
            throw new EventIdentityRequiredException();
        }

        var item = await events.FindLinkedForUpdateAsync(accountId, eventId, cancellationToken)
            ?? throw new EventNotFoundException();
        var member = await invitations.FindMembersAsync(eventId, accountId, cancellationToken)
            ?? throw new EventNotFoundException();
        if (!tokens.IsAvailable)
        {
            throw new InvitationProtectionUnavailableException();
        }

        return (item, member);
    }

    private async Task<InvitationStatusResponse> ReadCurrentAsync(
        EventEntity item, InvitationMemberSnapshot member, CancellationToken cancellationToken)
    {
        if (item.LockedAt.HasValue)
        {
            return new InvitationStatusResponse("locked", null);
        }
        if (member.Count != 1)
        {
            return new InvitationStatusResponse("full", null);
        }

        var pending = await invitations.FindPendingAsync(item.Id, cancellationToken);
        return pending is not null && IsLive(pending, clock.GetUtcNow())
            ? new InvitationStatusResponse("pending", ToResponse(pending))
            : new InvitationStatusResponse("available", null);
    }

    private CreateInvitationResponse Replay(
        EventEntity item, InvitationMemberSnapshot member, InvitationEntity invitation, DateTimeOffset now)
    {
        if (invitation.IssuerMemberId != member.MemberId)
        {
            throw new InvitationKeyReusedException();
        }
        if (!IsLive(invitation, now))
        {
            throw new InvitationUnavailableException();
        }

        RequireOpenSlot(item, member);
        return new CreateInvitationResponse(ToResponse(invitation), true);
    }

    private async Task<CreateInvitationResponse> IssueAsync(
        EventEntity item, InvitationMemberSnapshot member, long accountId, Guid requestKey, Guid correlation,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireOpenSlot(item, member);
        var previous = await invitations.FindPendingAsync(item.Id, cancellationToken);
        if (previous is not null)
        {
            await CloseAsync(previous, now, cancellationToken);
        }

        var secret = tokens.Create(item.Id, requestKey);
        var invitation = new InvitationEntity
        {
            EventId = item.Id,
            IssuerMemberId = member.MemberId,
            RequestKey = requestKey,
            HashKeyId = secret.HashKeyId,
            TokenHash = secret.Hash,
            EncryptionKeyId = secret.EncryptionKeyId,
            TokenCiphertext = secret.Ciphertext,
            State = "pending",
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
        await invitations.InsertAsync(invitation, cancellationToken);
        var (before, after) = AuditStates(previous);
        await events.InsertAuditAsync(new AuditEntryEntity(
            accountId, item.Id, "account", "invitation.created", "invitation", "success", correlation, now,
            now.AddDays(180), before, after, "events", invitation.Id), cancellationToken);
        return new CreateInvitationResponse(ToResponse(invitation), false);
    }

    private async Task CloseAsync(InvitationEntity previous, DateTimeOffset now, CancellationToken cancellationToken)
    {
        previous.State = now >= previous.ExpiresAt ? "expired" : "superseded";
        previous.Version++;
        previous.UpdatedAt = now;
        previous.InvalidatedAt = now;
        previous.EncryptionKeyId = null;
        previous.TokenCiphertext = null;
        await invitations.SaveClosedAsync(previous, cancellationToken);
    }

    private static void RequireOpenSlot(EventEntity item, InvitationMemberSnapshot member)
    {
        if (item.LockedAt.HasValue)
        {
            throw new InvitationEventLockedException();
        }
        if (member.Count != 1)
        {
            throw new InvitationEventFullException();
        }
    }

    private static bool IsLive(InvitationEntity invitation, DateTimeOffset now) =>
        invitation.State == "pending" && now >= invitation.CreatedAt && now < invitation.ExpiresAt;

    private static (string Before, string After) AuditStates(InvitationEntity? previous)
    {
        if (previous is null)
        {
            return ("{}", """{"state":"pending"}""");
        }

        var replacedInvitationId = previous.Id.ToString(CultureInfo.InvariantCulture);
        return (
            JsonSerializer.Serialize(new { replacedInvitationId, previousState = "pending" }),
            JsonSerializer.Serialize(new { state = "pending", replacedInvitationId, previousState = previous.State }));
    }

    private InvitationResponse ToResponse(InvitationEntity invitation) =>
        new(invitation.Id.ToString(CultureInfo.InvariantCulture), tokens.Recover(invitation), invitation.ExpiresAt);
}
