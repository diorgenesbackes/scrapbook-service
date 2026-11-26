namespace ScrapBook.Domain.Commands;

public sealed record EventStructuralLockCommand(long EventId, long Version, long RecordId,
    DateTime ConfirmedAt, DateTimeOffset LockedAt);
