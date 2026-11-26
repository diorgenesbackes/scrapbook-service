using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Commands;

public sealed record CreatePhotoSubmissionCommand(EventEntity Event, long NoticeId, string Category, string GuestName,
    string? Caption, IReadOnlyList<PhotoUploadIntentModel> Files, DateTime ConsentedAt, DateTime SubmittedAt, string Operation,
    Guid AttemptKey, string HashKeyId, byte[] PayloadHash, byte[] AuthorizationHash, DateTimeOffset AuthorizationExpiresAt,
    Guid CorrelationId, string Bucket, string Format = "photo");
