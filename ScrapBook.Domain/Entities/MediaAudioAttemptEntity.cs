namespace ScrapBook.Domain.Entities;

public sealed record MediaAudioAttemptEntity(long Id, Guid RequestKey, string OutputPrefix, DateTimeOffset StartedAt,
    DateTimeOffset? SubmittedAt, string? ProviderJobId, string State);
