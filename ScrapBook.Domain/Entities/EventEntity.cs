namespace ScrapBook.Domain.Entities;

public sealed record EventEntity
{
    public long Id { get; init; }
    public long CreatorId { get; init; }
    public long? CoverId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? WelcomeMessage { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime SubmissionOpensAt { get; init; }
    public DateTime SubmissionClosesAt { get; init; }
    public DateTime CompletionDeadlineAt { get; init; }
    public string OpeningMode { get; init; } = "scheduled";
    public DateTime? OpensAt { get; init; }
    public string AccessState { get; init; } = "flexible";
    public DateTime? AccessFrozenAt { get; init; }
    public long AccessVersion { get; init; } = 1;
    public DateTimeOffset? LockedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public long Version { get; init; }
}
