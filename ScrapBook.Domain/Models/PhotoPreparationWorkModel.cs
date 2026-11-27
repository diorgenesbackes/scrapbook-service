using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Models;

public sealed record PhotoPreparationWorkModel(MediaJobEntity Job, MediaSourceEntity Source, long AttemptId,
    Guid Lease, string OutputPrefix);
