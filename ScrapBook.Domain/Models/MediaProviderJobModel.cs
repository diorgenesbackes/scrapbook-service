namespace ScrapBook.Domain.Models;

public sealed record MediaProviderJobModel(string Id, string QueueArn, Guid RequestKey, string State, string? OutputUri);
