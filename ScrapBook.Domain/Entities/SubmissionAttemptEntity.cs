namespace ScrapBook.Domain.Entities;

public sealed class SubmissionAttemptEntity
{
    public long RecordId { get; init; }
    public string? Format { get; init; }
    public string? Category { get; init; }
    public int[]? PendingPositions { get; init; }
    public int[]? ProblemPositions { get; init; }
    public string[]? ProblemCodes { get; init; }
    public DateTime? CompletionDeadlineAt { get; init; }
    public string? ResultCode { get; init; }
    public required string State { get; init; }
    public DateTime? ConfirmedAt { get; init; }
    public string? HashKeyId { get; init; }
    public byte[]? PayloadHash { get; init; }
    public byte[]? AuthorizationHash { get; init; }
    public DateTimeOffset AuthorizationExpiresAt { get; init; }
}
