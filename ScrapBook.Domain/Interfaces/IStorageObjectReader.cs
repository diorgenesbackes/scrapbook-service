namespace ScrapBook.Domain.Interfaces;

public interface IStorageObjectReader
{
    Task<byte[]> ReadSha256Async(string bucket, string key, string versionId, long expectedSize, CancellationToken cancellationToken);
}
