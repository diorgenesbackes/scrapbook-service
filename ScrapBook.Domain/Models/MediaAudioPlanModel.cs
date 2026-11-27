namespace ScrapBook.Domain.Models;

public sealed record MediaAudioPlanModel(MediaAudioSettings Settings, string Bucket, MediaAudioInputModel? Input,
    MediaAudioProfileModel? Profile);
