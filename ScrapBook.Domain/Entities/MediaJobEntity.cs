namespace ScrapBook.Domain.Entities;

public sealed record MediaJobEntity(long Id, long EventId, long RecordId, long FileId, int ProfileVersion, long Version,
    string State, int AttemptCount, int RetryCycle, Guid ConfirmationMessageId, Guid ConfirmationCorrelationId,
    DateTimeOffset ConfirmedAt, long? ActiveAttemptId, int LastAttemptNumber, string Profile, string FileKind = "audio");
