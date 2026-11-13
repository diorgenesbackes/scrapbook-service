namespace ScrapBook.Domain.Entities;

public sealed class OpeningDispatchPersistenceEntity
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long MemberId { get; set; }
    public long AccessVersion { get; set; }
    public DateTime DueAt { get; set; }
    public string State { get; set; } = "pending";
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}
