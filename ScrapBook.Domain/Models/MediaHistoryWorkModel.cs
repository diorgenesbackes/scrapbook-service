namespace ScrapBook.Domain.Models;

public sealed record MediaHistoryWorkModel(long? JobId, Guid? MessageId, Guid? CorrelationId, DateTimeOffset? ConfirmedAt,
    bool HasVariants);
