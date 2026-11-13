namespace ScrapBook.Domain.Entities;

public sealed class EventAuditPersistenceEntity
{
    public long Id { get; set; }
    public long? AccountId { get; set; }
    public long? EventId { get; set; }
    public string ActorType { get; set; } = "account";
    public string Action { get; set; } = "";
    public string SubjectType { get; set; } = "event";
    public long? SubjectId { get; set; }
    public string Outcome { get; set; } = "success";
    public string? ReasonCode { get; set; }
    public Guid CorrelationId { get; set; }
    public string BeforeState { get; set; } = "{}";
    public string AfterState { get; set; } = "{}";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
