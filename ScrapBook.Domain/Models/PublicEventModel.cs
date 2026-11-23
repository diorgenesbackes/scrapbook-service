namespace ScrapBook.Domain.Models;

public sealed record PublicEventModel(
    string Name,
    string? WelcomeMessage,
    DateTime StartsAt,
    DateTime SubmissionOpensAt,
    DateTime SubmissionClosesAt,
    DateTime CompletionDeadlineAt);
