using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Domain.Interfaces;

public interface IPhotoSubmissionStorage
{
    Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(string bucket, string key, string mediaType, int size,
        byte[] sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<byte[]> ReadAsync(string bucket, string key, string versionId, int expectedSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<PhotoVersionModel>> ListVersionsAsync(string bucket, string key, CancellationToken cancellationToken);
    Task<PhotoObjectModel?> ReadMetadataAsync(string bucket, string key, string versionId, CancellationToken cancellationToken);
    Task<string> CopyAsync(string bucket, string sourceKey, string sourceVersion, string destinationKey, string mediaType,
        CancellationToken cancellationToken);
    Task DeleteAsync(string bucket, string key, string versionId, CancellationToken cancellationToken);
}
