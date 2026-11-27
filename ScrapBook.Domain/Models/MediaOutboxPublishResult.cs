namespace ScrapBook.Domain.Models;

public enum MediaOutboxPublishResult
{
    Published,
    RetryScheduled,
    Failed,
    InvalidFact,
    LeaseLost,
    ReconciliationRequired
}
