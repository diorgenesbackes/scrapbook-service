using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;

namespace ScrapBook.UnitTests.Services;

internal sealed class PhotoSubmissionStorageStub(byte[] bytes) : IPhotoSubmissionStorage
{
    private readonly Dictionary<string, PhotoObjectModel> copiedObjects = [];

    public byte[] Bytes { get; set; } = bytes;
    public string? UnprovenVersion { get; set; }
    public bool DeleteUnavailable { get; set; }
    public List<(string Bucket, string Key, string MediaType, int Size, byte[] Hash, DateTimeOffset ExpiresAt)> Authorizations { get; } = [];
    public List<(string Key, string VersionId)> Reads { get; } = [];
    public List<(string Source, string VersionId, string Destination)> Copies { get; } = [];
    public List<(string Bucket, string Key, string VersionId)> Deletions { get; } = [];

    public Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(string bucket, string key, string mediaType, int size,
        byte[] sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        Authorizations.Add((bucket, key, mediaType, size, sha256, expiresAt));
        return Task.FromResult(new PhotoUploadAuthorizationResponse("PUT", "https://upload.example/temporary",
            new Dictionary<string, string> { ["Content-Type"] = mediaType, ["x-amz-checksum-sha256"] = Convert.ToBase64String(sha256) },
            expiresAt));
    }

    public Task<byte[]> ReadAsync(string bucket, string key, string versionId, int expectedSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((key, versionId));
        return Task.FromResult(Bytes);
    }

    public Task<IReadOnlyList<PhotoVersionModel>> ListVersionsAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        var version = copiedObjects.TryGetValue(key, out var original) ? original.VersionId : UnprovenVersion;
        IReadOnlyList<PhotoVersionModel> versions = version is null ? [] : [new(version)];
        return Task.FromResult(versions);
    }

    public Task<PhotoObjectModel?> ReadMetadataAsync(string bucket, string key, string versionId, CancellationToken cancellationToken) =>
        Task.FromResult(copiedObjects.GetValueOrDefault(key));

    public Task<string> CopyAsync(string bucket, string sourceKey, string sourceVersion, string destinationKey, string mediaType,
        CancellationToken cancellationToken)
    {
        Copies.Add((sourceKey, sourceVersion, destinationKey));
        var original = new PhotoObjectModel("original-version", Bytes.Length, DateTimeOffset.UnixEpoch, sourceVersion, null);
        copiedObjects[destinationKey] = original;
        return Task.FromResult(original.VersionId);
    }

    public Task DeleteAsync(string bucket, string key, string versionId, CancellationToken cancellationToken)
    {
        if (DeleteUnavailable) throw new PhotoStorageUnavailableException();
        Deletions.Add((bucket, key, versionId));
        return Task.CompletedTask;
    }
}
