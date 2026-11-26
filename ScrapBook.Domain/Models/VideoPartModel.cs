namespace ScrapBook.Domain.Models;

public sealed record VideoPartModel(int PartNumber, long Size, byte[] Sha256, string ETag);
