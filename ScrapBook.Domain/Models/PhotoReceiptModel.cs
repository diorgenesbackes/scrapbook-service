namespace ScrapBook.Domain.Models;

public sealed record PhotoReceiptModel(string Bucket, string Key, string VersionId, long Size, DateTimeOffset ReceivedAt);
