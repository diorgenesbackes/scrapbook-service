namespace ScrapBook.Domain.Models;

public sealed record MediaConfirmationModel(Guid MessageId, long EventId, long RecordId, long FileId,
    DateTimeOffset OccurredAt, DateTimeOffset ConfirmedAt, Guid CorrelationId)
{
    public const string Type = "record.media-confirmed.v1";
    public const short Version = 1;
}
