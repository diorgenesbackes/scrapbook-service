namespace ScrapBook.Domain.Entities;

public sealed record ProcessedMessageEntity(long Id, byte[] PayloadHash, string State, string? ErrorCode = null);
