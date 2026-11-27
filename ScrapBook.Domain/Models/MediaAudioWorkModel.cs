using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Models;

public sealed record MediaAudioWorkModel(MediaJobEntity Job, MediaSourceEntity? Source, MediaAudioAttemptEntity Attempt,
    Guid Lease, DateTimeOffset LeaseUntil, MediaAudioPlanModel Plan);
