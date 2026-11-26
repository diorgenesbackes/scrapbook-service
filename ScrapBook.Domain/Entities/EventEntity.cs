namespace ScrapBook.Domain.Entities;

public sealed class EventEntity
{
    public long Id { get; set; }
    public long CreatorAccountId { get; init; }
    public long Version { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? WelcomeMessage { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime SubmissionOpensAt { get; set; }
    public DateTime SubmissionClosesAt { get; set; }
    public DateTime CompletionDeadlineAt { get; set; }
    public required string OpeningMode { get; set; }
    public DateTime? OpensAt { get; set; }
    public required string AccessState { get; set; }
    public DateTime? AccessFrozenAt { get; set; }
    public long AccessVersion { get; set; } = 1;
    public DateTime? LockedAt { get; init; }
    public long? FirstRecordId { get; init; }
    public DateTime? FirstRecordConfirmedAt { get; init; }
    public byte[]? CreationKeyHash { get; init; }
    public byte[]? CreationPayloadHash { get; init; }
}
