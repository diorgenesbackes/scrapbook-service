namespace ScrapBook.Domain.Entities;

public sealed record SubmissionOutboxEntity(Guid MessageId, string Type, long EventId,
    string Payload, Guid CorrelationId, DateTimeOffset OccurredAt);
