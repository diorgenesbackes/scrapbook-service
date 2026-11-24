namespace ScrapBook.Domain.Entities;

public sealed record AuditEntryEntity(
    long AccountId, long EventId, string ActorType, string Action, string SubjectType,
    string Outcome, Guid CorrelationId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string BeforeState, string AfterState, string Area, long? SubjectId = null);
