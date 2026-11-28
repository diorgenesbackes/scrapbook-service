namespace ScrapBook.Domain.Models;

public sealed record MediaProcessingBacklogModel(string MediaType, bool IsHistorical, long PendingCount, long ActiveCount,
    long ReconcilingCount, DateTimeOffset? OldestPendingAt);
