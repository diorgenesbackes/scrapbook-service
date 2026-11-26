using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;

namespace ScrapBook.UnitTests.Services;

internal sealed class MultipartCleanupStorageStub : IVideoSubmissionStorage, IVideoSubmissionDecoder
{
    public Queue<IReadOnlyList<string>> OpenUploads { get; } = new();
    public Dictionary<string, VideoPartModel[]> RemainingParts { get; } = [];
    public List<string> Aborted { get; } = [];
    public List<string> CheckedParts { get; } = [];
    public List<string> CheckedKeys { get; } = [];
    public Action? OnListUploads { get; set; }

    public Task<IReadOnlyList<string>> ListMultipartUploadsAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        CheckedKeys.Add(key);
        OnListUploads?.Invoke();
        return Task.FromResult<IReadOnlyList<string>>(OpenUploads.TryDequeue(out var open) ? open : []);
    }

    public Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        Aborted.Add(uploadId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VideoPartModel>?> ListPartsAsync(string bucket, string key, string uploadId,
        CancellationToken cancellationToken)
    {
        CheckedParts.Add(uploadId);
        return Task.FromResult<IReadOnlyList<VideoPartModel>?>(RemainingParts.GetValueOrDefault(uploadId));
    }

    public Task<string> CreateMultipartUploadAsync(string bucket, string key, string mediaType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PhotoUploadAuthorizationResponse> AuthorizePartAsync(string bucket, string key, string uploadId, int partNumber,
        int size, byte[] sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string?> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, IReadOnlyList<VideoPartModel> parts,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<byte[]> ReadSha256Async(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<VideoMetadataModel> DecodeAsync(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
