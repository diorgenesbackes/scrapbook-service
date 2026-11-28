namespace ScrapBook.Domain.Commands;

public sealed record MediaRecoveryBatchCommand(long EventId, long AfterId, int Limit, string Incident, string Revision, bool Apply,
    DateTimeOffset? HistoricalBefore = null);
