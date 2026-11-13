namespace ScrapBook.Application.Models;

public sealed record EventAuditModel(long AccountId, long EventId, string Action, Guid CorrelationId,
    IReadOnlyDictionary<string, object?> Before, IReadOnlyDictionary<string, object?> After, string ReasonCode);
