namespace ScrapBook.Domain.Responses;

public sealed record InvitationStatusResponse(string State, InvitationResponse? Invitation);
