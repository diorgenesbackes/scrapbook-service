namespace ScrapBook.Domain.Responses;

public sealed record InvitationResponse(string Id, string Token, DateTimeOffset ExpiresAt);
