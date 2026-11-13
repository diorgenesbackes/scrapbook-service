namespace ScrapBook.Infrastructure.Entities;

internal sealed class EventMemberPersistenceEntity
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long AccountId { get; set; }
    public short Slot { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
