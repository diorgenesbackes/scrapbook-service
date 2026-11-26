using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Commands;

public sealed record ConfirmPhotoSubmissionCommand(PhotoSubmissionEntity Submission, Guid Lease, PhotoImageModel Image,
    PhotoObjectModel Original, DateTime ReceivedAt, DateTime ConfirmedAt, DateTimeOffset CommittedAt);
