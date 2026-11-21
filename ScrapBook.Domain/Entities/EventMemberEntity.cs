namespace ScrapBook.Domain.Entities;

public sealed class EventMemberEntity
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public EventEntity? Event { get; init; }
    public long AccountId { get; init; }
    public short Slot { get; init; }
    public DateTimeOffset JoinedAt { get; init; }
}
