using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IInvitationRepository
{
    Task<bool> IsAccountActiveAsync(long accountId, CancellationToken cancellationToken);
    Task<InvitationMemberSnapshot?> FindMembersAsync(long eventId, long accountId, CancellationToken cancellationToken);
    Task<InvitationEntity?> FindByRequestAsync(long eventId, Guid requestKey, CancellationToken cancellationToken);
    Task<InvitationEntity?> FindPendingAsync(long eventId, CancellationToken cancellationToken);
    Task InsertAsync(InvitationEntity invitation, CancellationToken cancellationToken);
    Task SaveClosedAsync(InvitationEntity invitation, CancellationToken cancellationToken);
}
