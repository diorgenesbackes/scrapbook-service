using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Commands;

public sealed record ConfirmPhotoSubmissionCommand(PhotoSubmissionEntity Submission, Guid Lease,
    IReadOnlyList<ConfirmPhotoFileModel> Files, DateTime ReceivedAt, DateTime ConfirmedAt, DateTimeOffset CommittedAt);
