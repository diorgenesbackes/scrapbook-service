namespace ScrapBook.Domain.Commands;

public sealed record ConfirmTextSubmissionCommand(
    long EventId, long NoticeId, string GuestName, string Text,
    DateTime ConsentedAt, DateTime SubmittedAt, DateTime ReceivedAt, DateTime ConfirmedAt,
    DateTime WindowOpensAt, DateTime WindowClosesAt, DateTime CompletionDeadlineAt,
    Guid AttemptKey, string Operation, string HashKeyId, byte[] PayloadHash, byte[] AuthorizationHash,
    DateTimeOffset AuthorizationExpiresAt, DateTimeOffset CommittedAt);
