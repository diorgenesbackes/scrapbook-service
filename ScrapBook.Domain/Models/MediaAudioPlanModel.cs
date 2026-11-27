using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Models;

public sealed record MediaAudioPlanModel(MediaAudioSettings Settings, string Bucket, MediaAudioInputModel? Input,
    MediaAudioProfileModel? Profile)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaVideoInputModel? VideoInput { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaVideoProfileModel? VideoProfile { get; init; }
}
