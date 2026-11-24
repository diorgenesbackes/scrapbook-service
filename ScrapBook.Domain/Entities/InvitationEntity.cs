namespace ScrapBook.Domain.Entities;

public sealed class InvitationEntity
{
    public long Id { get; set; }
    public long EventId { get; init; }
    public long IssuerMemberId { get; init; }
    public long? RecipientAccountId { get; set; }
    public Guid RequestKey { get; init; }
    public required string HashKeyId { get; init; }
    public required byte[] TokenHash { get; init; }
    public string? EncryptionKeyId { get; set; }
    public byte[]? TokenCiphertext { get; set; }
    public required string State { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
}
