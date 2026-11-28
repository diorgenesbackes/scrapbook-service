namespace ScrapBook.Domain.Entities;

public sealed record MediaRecoveryJobEntity(long Id, long RecordId, long FileId, long Version, int RetryCycle, string State,
    string FileKind, Guid? ConfirmationMessageId, Guid? ConfirmationCorrelationId, DateTimeOffset? ConfirmedAt,
    bool HasLease, bool HasActiveAttempt, int LastAttemptNumber);
