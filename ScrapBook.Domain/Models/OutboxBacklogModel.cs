namespace ScrapBook.Domain.Models;

public sealed record OutboxBacklogModel(long PendingCount, long FailedCount, DateTimeOffset? OldestPendingAt);
