namespace ScrapBook.Domain.Entities;

public sealed record StructuralLockOutboxEntity(Guid MessageId, string Type, long EventId, string Payload,
    Guid CorrelationId, Guid CausationId, DateTimeOffset OccurredAt);
