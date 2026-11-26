namespace ScrapBook.Domain.Models;

public sealed record FirstRecordConfirmedModel(Guid MessageId, long EventId, long RecordId, DateTimeOffset OccurredAt,
    DateTimeOffset ConfirmedAt, Guid CorrelationId)
{
    public const string Type = "event.first-record-confirmed.v1";
    public const short Version = 1;
}
