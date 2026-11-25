namespace ScrapBook.Domain.Entities;

public sealed class SubmissionAttemptEntity
{
    public required string State { get; init; }
    public DateTime? ConfirmedAt { get; init; }
    public string? HashKeyId { get; init; }
    public byte[]? PayloadHash { get; init; }
    public byte[]? AuthorizationHash { get; init; }
    public DateTimeOffset AuthorizationExpiresAt { get; init; }
}
