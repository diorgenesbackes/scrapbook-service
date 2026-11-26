using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Domain.Interfaces;

public interface IVideoSubmissionStorage
{
    Task<IReadOnlyList<string>> ListMultipartUploadsAsync(string bucket, string key, CancellationToken cancellationToken);
    Task<string> CreateMultipartUploadAsync(string bucket, string key, string mediaType, CancellationToken cancellationToken);
    Task<PhotoUploadAuthorizationResponse> AuthorizePartAsync(string bucket, string key, string uploadId, int partNumber,
        int size, byte[] sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<VideoPartModel>?> ListPartsAsync(string bucket, string key, string uploadId,
        CancellationToken cancellationToken);
    Task<string?> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, IReadOnlyList<VideoPartModel> parts,
        CancellationToken cancellationToken);
    Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken);
    Task<byte[]> ReadSha256Async(string bucket, string key, string versionId, long expectedSize, CancellationToken cancellationToken);
}
