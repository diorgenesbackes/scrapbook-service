namespace ScrapBook.Application.Diagnostics;

public enum MediaProcessingOutcome
{
    Available,
    Failed,
    Admitted,
    Duplicate,
    Quarantined,
    Acknowledged,
    Ignored,
    Retry,
    Published,
    RetryScheduled,
    InvalidFact,
    LeaseLost,
    ReconciliationRequired,
    Started,
    Interrupted,
    Pending,
    Deferred,
    InterventionRequired
}
