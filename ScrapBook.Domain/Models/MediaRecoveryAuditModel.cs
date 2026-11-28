namespace ScrapBook.Domain.Models;

public sealed record MediaRecoveryAuditModel(string Operator, long EventId, long? FileId, string Action, string Outcome,
    string ReasonCode, string Incident, string Revision, Guid CorrelationId, int? Cycle = null);
