namespace ScrapBook.Domain.Entities;

public sealed record OutboxMessageEntity(Guid MessageId, string Type, short ProtocolVersion, string? AggregateType,
    long? EventId, string Payload, Guid CorrelationId, DateTimeOffset OccurredAt, int AttemptCount, Guid? LeaseToken, string State = "pending");
