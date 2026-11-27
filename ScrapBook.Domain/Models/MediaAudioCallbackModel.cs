namespace ScrapBook.Domain.Models;

public sealed record MediaAudioCallbackModel(string Account, string Region, string QueueArn, string JobId, Guid RequestKey,
    string State);
