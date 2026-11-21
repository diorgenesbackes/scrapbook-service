namespace ScrapBook.Domain.Entities;

public sealed class EventEntity
{
    public long Id { get; set; }
    public long CreatorAccountId { get; init; }
    public long Version { get; set; }
    public required string Code { get; set; }
    public required string Name { get; init; }
    public string? WelcomeMessage { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime SubmissionOpensAt { get; init; }
    public DateTime SubmissionClosesAt { get; init; }
    public DateTime CompletionDeadlineAt { get; init; }
    public required string OpeningMode { get; set; }
    public DateTime? OpensAt { get; set; }
    public required string AccessState { get; set; }
    public DateTime? AccessFrozenAt { get; set; }
    public long AccessVersion { get; set; } = 1;
    public DateTime? LockedAt { get; init; }
    public byte[]? CreationKeyHash { get; init; }
    public byte[]? CreationPayloadHash { get; init; }
}
