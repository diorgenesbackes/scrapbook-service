namespace ScrapBook.Domain.Models;

public enum EventStructuralLockRecoveryResult
{
    Reopened,
    Reconciled,
    AlreadyCompleted,
    AlreadyTerminal,
    AlreadyScheduled,
    Rejected,
    InvalidArguments
}
