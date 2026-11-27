namespace ScrapBook.Domain.Entities;

public sealed record MediaSourceEntity(long EventId, long RecordId, long FileId, string Kind, string RecordState,
    string ValidationState, string ProcessingState, int ProfileVersion, DateTime? RecordConfirmedAt,
    DateTime? FileConfirmedAt, string Bucket, string ObjectKey, string VersionId, long Size,
    byte[] Sha256, byte[]? OriginalSha256);
