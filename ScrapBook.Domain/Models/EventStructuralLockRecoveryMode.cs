namespace ScrapBook.Domain.Models;

public enum EventStructuralLockRecoveryMode
{
    Publish,
    Consume,
    ReconcileDlq
}
