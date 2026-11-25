using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Infrastructure.Storage;

public sealed class S3PhotoSubmissionStorage(IAmazonS3 storage, IAmazonS3 browserStorage, Protocol protocol = Protocol.HTTPS)
    : IPhotoSubmissionStorage, IDisposable
{
    private const string SourceVersionHeader = "x-amz-meta-source-version";
    private const string ChecksumHeader = "x-amz-checksum-sha256";
    private const int MaximumVersions = 1000;

    public async Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(string bucket, string key, string mediaType, int size,
        byte[] sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var checksum = Convert.ToBase64String(sha256);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Protocol = protocol,
            Expires = expiresAt.UtcDateTime,
            ContentType = mediaType
        };
        request.Headers[ChecksumHeader] = checksum;
        request.Headers["Content-Length"] = size.ToString(CultureInfo.InvariantCulture);
        try
        {
            var url = await browserStorage.GetPreSignedURLAsync(request).WaitAsync(cancellationToken);
            return new PhotoUploadAuthorizationResponse("PUT", url,
                new Dictionary<string, string> { ["Content-Type"] = mediaType, [ChecksumHeader] = checksum }, expiresAt);
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            throw new PhotoStorageUnavailableException(error);
        }
    }

    public Task<byte[]> ReadAsync(string bucket, string key, string versionId, int expectedSize, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            using var response = await storage.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket, Key = key, VersionId = versionId
            }, cancellationToken);
            if (response.VersionId != versionId) throw new PhotoStorageUnavailableException();
            if (response.ContentLength != expectedSize) throw new PhotoContentInvalidException();
            var bytes = new byte[expectedSize];
            try
            {
                await response.ResponseStream.ReadExactlyAsync(bytes, cancellationToken);
                var extra = await response.ResponseStream.ReadAsync(new byte[1], cancellationToken);
                return extra == 0 ? bytes : throw new PhotoContentInvalidException();
            }
            catch (IOException error)
            {
                throw new PhotoStorageUnavailableException(error);
            }
        });

    public Task<IReadOnlyList<PhotoVersionModel>> ListVersionsAsync(string bucket, string key,
        CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<PhotoVersionModel>>(async () =>
        {
            var result = await storage.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket, Prefix = key, MaxKeys = MaximumVersions
            }, cancellationToken);
            if (result.IsTruncated == true) throw new PhotoStorageUnavailableException();
            return (result.Versions ?? []).Where(version => version.Key == key && version.IsDeleteMarker != true)
                .Select(version => new PhotoVersionModel(RequireVersion(version.VersionId))).ToArray();
        });

    public Task<PhotoObjectModel?> ReadMetadataAsync(string bucket, string key, string versionId,
        CancellationToken cancellationToken) =>
        ExecuteAsync<PhotoObjectModel?>(async () =>
        {
            try
            {
                var result = await storage.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = bucket, Key = key, VersionId = versionId, ChecksumMode = ChecksumMode.ENABLED
                }, cancellationToken);
                var sha256 = new byte[32];
                var verified = result.ChecksumSHA256 is { } checksum
                    && Convert.TryFromBase64String(checksum, sha256, out var written) && written == sha256.Length;
                var lastModified = result.LastModified ?? throw new PhotoStorageUnavailableException();
                return new PhotoObjectModel(RequireVersion(result.VersionId), result.ContentLength,
                    new DateTimeOffset(lastModified.ToUniversalTime()), result.Metadata[SourceVersionHeader], verified ? sha256 : null);
            }
            catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        });

    public Task<string> CopyAsync(string bucket, string sourceKey, string sourceVersion, string destinationKey, string mediaType,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var request = new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = sourceKey,
                SourceVersionId = sourceVersion,
                DestinationBucket = bucket,
                DestinationKey = destinationKey,
                ContentType = mediaType,
                MetadataDirective = S3MetadataDirective.REPLACE,
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256
            };
            request.Metadata[SourceVersionHeader] = sourceVersion;
            var result = await storage.CopyObjectAsync(request, cancellationToken);
            return RequireVersion(result.VersionId);
        });

    public Task DeleteAsync(string bucket, string key, string versionId, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            try
            {
                await storage.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket, Key = key, VersionId = versionId
                }, cancellationToken);
            }
            catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
            }
            return true;
        });

    public void Dispose()
    {
        if (!ReferenceEquals(browserStorage, storage)) browserStorage.Dispose();
    }

    private static string RequireVersion(string? versionId) => versionId is { Length: > 0 } and not "null"
        ? versionId : throw new PhotoStorageUnavailableException();

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            throw new PhotoStorageUnavailableException(error);
        }
    }
}
