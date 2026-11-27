namespace ScrapBook.Domain.Entities;

public sealed record MediaFailureEntity(Guid MessageId, string Type, long EventId, string Payload,
    Guid CorrelationId, Guid CausationId, DateTimeOffset OccurredAt);
