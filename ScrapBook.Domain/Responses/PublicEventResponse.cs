namespace ScrapBook.Domain.Responses;

public sealed record PublicEventResponse(string Code, string Name, string EffectiveWelcomeMessage, CoverResponse Cover);
