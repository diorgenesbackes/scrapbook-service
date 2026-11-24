using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IInvitationRepository
{
    Task<bool> IsAccountActiveAsync(long accountId, CancellationToken cancellationToken);
    Task<InvitationAccountSnapshot?> FindAccountForShareAsync(long accountId, CancellationToken cancellationToken);
    Task<InvitationMemberSnapshot> FindMembersAsync(long eventId, long accountId, CancellationToken cancellationToken);
    Task<InvitationEntity?> FindByRequestAsync(long eventId, Guid requestKey, CancellationToken cancellationToken);
    Task<InvitationEntity?> FindPendingAsync(long eventId, CancellationToken cancellationToken);
    Task<InvitationLocator?> FindByDigestAsync(InvitationDigest digest, CancellationToken cancellationToken);
    Task<InvitationEntity?> FindForUpdateAsync(InvitationLocator invitation, CancellationToken cancellationToken);
    Task InsertAsync(InvitationEntity invitation, CancellationToken cancellationToken);
    Task InsertMemberAsync(EventMemberEntity member, CancellationToken cancellationToken);
    Task SaveClosedAsync(InvitationEntity invitation, CancellationToken cancellationToken);
}
