namespace ScrapBook.Domain.Models;

public sealed record MediaAudioProfileModel(string Account, string Region, string QueueArn, string JobRoleArn,
    int SampleRate, int Channels, int Bitrate);
