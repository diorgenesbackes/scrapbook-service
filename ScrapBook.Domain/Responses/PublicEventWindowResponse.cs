namespace ScrapBook.Domain.Responses;

public sealed record PublicEventWindowResponse(
    string State,
    string OpensAt,
    string ClosesAt,
    string CompletionDeadlineAt,
    DateTimeOffset? NextTransitionAt);
