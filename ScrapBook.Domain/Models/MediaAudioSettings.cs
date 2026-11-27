using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Models;

public sealed record MediaAudioSettings(string Account, string Region, string QueueArn, string JobRoleArn)
{
    [JsonIgnore]
    public bool VideoEnabled { get; init; }
}
