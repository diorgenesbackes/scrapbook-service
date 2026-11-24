namespace ScrapBook.Domain.Responses;

public sealed record CreateInvitationResponse(InvitationResponse Invitation, bool IsReplay);
