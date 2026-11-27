namespace ScrapBook.Domain.Models;

public sealed record MediaAudioRequestModel(Guid RequestKey, string Bucket, string InputKey, string OutputKey,
    MediaAudioProfileModel Profile);
