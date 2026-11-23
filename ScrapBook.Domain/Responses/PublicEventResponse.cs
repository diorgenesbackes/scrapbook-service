namespace ScrapBook.Domain.Responses;

public sealed record PublicEventResponse(
    string Name,
    string StartsAt,
    string TimeZone,
    EventCoverResponse Cover,
    string WelcomeMessage,
    PublicEventWindowResponse SubmissionWindow,
    DateTimeOffset ServerTime);
