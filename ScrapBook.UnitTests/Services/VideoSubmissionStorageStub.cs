using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;

namespace ScrapBook.UnitTests.Services;

internal sealed class VideoSubmissionStorageStub(byte[] originalSha256) : IVideoSubmissionStorage
{
    public IReadOnlyList<string> OpenUploadIds { get; set; } = [];
    public IReadOnlyList<VideoPartModel>? Parts { get; set; } = [];
    public string? CompletedVersion { get; set; }
    public byte[] OriginalSha256 { get; set; } = originalSha256;
    public List<(string Bucket, string Key, string MediaType)> CreatedUploads { get; } = [];
    public List<(string Bucket, string Key, string UploadId, int Number, int Size, byte[] Sha256, DateTimeOffset ExpiresAt)> Grants { get; } = [];
    public List<(string Bucket, string Key, string UploadId, IReadOnlyList<VideoPartModel> Parts)> Completions { get; } = [];
    public List<(string Bucket, string Key, string Version, long Size)> OriginalReads { get; } = [];

    public Task<IReadOnlyList<string>> ListMultipartUploadsAsync(string bucket, string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenUploadIds);
    }

    public Task<string> CreateMultipartUploadAsync(string bucket, string key, string mediaType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreatedUploads.Add((bucket, key, mediaType));
        return Task.FromResult("created-upload");
    }

    public Task<PhotoUploadAuthorizationResponse> AuthorizePartAsync(string bucket, string key, string uploadId, int partNumber,
        int size, byte[] sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Grants.Add((bucket, key, uploadId, partNumber, size, sha256, expiresAt));
        return Task.FromResult(new PhotoUploadAuthorizationResponse("PUT", "https://upload.example/part",
            new Dictionary<string, string>(), expiresAt));
    }

    public Task<IReadOnlyList<VideoPartModel>?> ListPartsAsync(string bucket, string key, string uploadId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Parts);
    }

    public Task<string?> CompleteMultipartUploadAsync(string bucket, string key, string uploadId,
        IReadOnlyList<VideoPartModel> parts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Completions.Add((bucket, key, uploadId, parts));
        return Task.FromResult(CompletedVersion);
    }

    public Task AbortMultipartUploadAsync(string bucket, string key, string uploadId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenUploadIds = OpenUploadIds.Where(current => current != uploadId).ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadSha256Async(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OriginalReads.Add((bucket, key, versionId, expectedSize));
        return Task.FromResult(OriginalSha256);
    }
}
