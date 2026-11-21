namespace ScrapBook.Domain.Responses;

public sealed record EventResponse(
    string Id,
    string Version,
    string Name,
    string StartsAt,
    string TimeZone,
    EventCoverResponse Cover,
    string WelcomeMessage,
    string SubmissionOpensAt,
    string SubmissionClosesAt,
    string CompletionDeadlineAt,
    EventOpeningResponse OpeningPolicy,
    bool CanEditDetails,
    DateTimeOffset ServerTime);
