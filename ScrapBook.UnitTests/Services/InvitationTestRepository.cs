using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class InvitationTestRepository : IInvitationRepository
{
    public List<InvitationEntity> Items { get; } = [];
    public bool Active { get; set; } = true;
    public int MemberCount { get; set; } = 1;
    public long? MemberId { get; set; } = 32;
    public bool EmailVerified { get; set; } = true;
    public EventMemberEntity? NewMember { get; private set; }

    public Task<bool> IsAccountActiveAsync(long accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Active);
    }

    public Task<InvitationAccountSnapshot?> FindAccountForShareAsync(long accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<InvitationAccountSnapshot?>(new(Active ? "active" : "disabled", EmailVerified));
    }

    public Task<InvitationMemberSnapshot> FindMembersAsync(long eventId, long accountId, CancellationToken cancellationToken) =>
        Task.FromResult(new InvitationMemberSnapshot(MemberId, MemberCount));

    public Task<InvitationEntity?> FindByRequestAsync(long eventId, Guid requestKey, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(item => item.EventId == eventId && item.RequestKey == requestKey));

    public Task<InvitationEntity?> FindPendingAsync(long eventId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(item => item.EventId == eventId && item.State == "pending"));

    public Task<InvitationLocator?> FindByDigestAsync(InvitationDigest digest, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Where(item => item.HashKeyId == digest.KeyId && item.TokenHash.SequenceEqual(digest.Hash))
            .Select(item => new InvitationLocator(item.Id, item.EventId)).SingleOrDefault());

    public Task<InvitationEntity?> FindForUpdateAsync(InvitationLocator invitation, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(item => item.Id == invitation.Id && item.EventId == invitation.EventId));

    public Task InsertAsync(InvitationEntity invitation, CancellationToken cancellationToken)
    {
        invitation.Id = Items.Count + 1;
        Items.Add(invitation);
        return Task.CompletedTask;
    }

    public Task SaveClosedAsync(InvitationEntity invitation, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task InsertMemberAsync(EventMemberEntity member, CancellationToken cancellationToken)
    {
        NewMember = member;
        MemberId = 55;
        MemberCount++;
        return Task.CompletedTask;
    }
}
