namespace ScrapBook.Domain.Models;

public sealed record MediaAudioWakeModel(long JobId, long AttemptId, string Profile, string? ProviderJobId, Guid CorrelationId,
    long FileId, string FileKind = "audio");
