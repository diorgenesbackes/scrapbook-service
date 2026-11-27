namespace ScrapBook.Domain.Models;

public sealed record MediaProviderJobModel(string Id, string QueueArn, Guid RequestKey, string State, string? OutputUri,
    string? JobRoleArn = null, string? ClientRequestToken = null, string? InputUri = null, string? OutputDestination = null);
