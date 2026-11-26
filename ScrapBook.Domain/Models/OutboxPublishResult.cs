namespace ScrapBook.Domain.Models;

public enum OutboxPublishResult
{
    Published,
    RetryScheduled,
    Failed,
    LeaseLost
}
