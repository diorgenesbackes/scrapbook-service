using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class InvitationTestRepository : IInvitationRepository
{
    public List<InvitationEntity> Items { get; } = [];
    public bool Active { get; set; } = true;
    public int MemberCount { get; set; } = 1;
    public long MemberId { get; set; } = 32;

    public Task<bool> IsAccountActiveAsync(long accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Active);
    }

    public Task<InvitationMemberSnapshot?> FindMembersAsync(long eventId, long accountId, CancellationToken cancellationToken) =>
        Task.FromResult<InvitationMemberSnapshot?>(new(MemberId, MemberCount));

    public Task<InvitationEntity?> FindByRequestAsync(long eventId, Guid requestKey, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(item => item.EventId == eventId && item.RequestKey == requestKey));

    public Task<InvitationEntity?> FindPendingAsync(long eventId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(item => item.EventId == eventId && item.State == "pending"));

    public Task InsertAsync(InvitationEntity invitation, CancellationToken cancellationToken)
    {
        invitation.Id = Items.Count + 1;
        Items.Add(invitation);
        return Task.CompletedTask;
    }

    public Task SaveClosedAsync(InvitationEntity invitation, CancellationToken cancellationToken) => Task.CompletedTask;
}
