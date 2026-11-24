using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IInvitationTokenCodec
{
    bool IsAvailable { get; }
    InvitationSecret Create(long eventId, Guid requestKey);
    string Recover(InvitationEntity invitation);
    InvitationDigest? Digest(string token);
}
