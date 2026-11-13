namespace ScrapBook.Domain.Responses;

public sealed record EventResponse(string Id, string Name, string StartsAt, string TimeZone, string? WelcomeMessage,
    string EffectiveWelcomeMessage, CoverResponse Cover, string OpeningMode, string? OpeningDate, string? OpensAt,
    SubmissionWindowResponse SubmissionWindow, string AccessState, bool CanEditStructure, bool CanEditAccess,
    string Version, string ServerTime);
