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
    : IPhotoSubmissionStorage, IVideoSubmissionStorage, IDisposable
{
    private const string SourceVersionHeader = "x-amz-meta-source-version";
    private const string ChecksumHeader = "x-amz-checksum-sha256";
    private const int MaximumVersions = 1000;

    public async Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(string bucket, string key, string mediaType, int size,
        byte[] sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var request = PutAuthorizationRequest(bucket, key, size, sha256, expiresAt);
        request.ContentType = mediaType;
        return await AuthorizePutAsync(request,
            new Dictionary<string, string> { ["Content-Type"] = mediaType, [ChecksumHeader] = request.Headers[ChecksumHeader] },
            cancellationToken);
    }

    public Task<PhotoUploadAuthorizationResponse> AuthorizePartAsync(string bucket, string key, string uploadId,
        int partNumber, int size, byte[] sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var request = PutAuthorizationRequest(bucket, key, size, sha256, expiresAt);
        request.UploadId = uploadId;
        request.PartNumber = partNumber;
        return AuthorizePutAsync(request, new Dictionary<string, string> { [ChecksumHeader] = request.Headers[ChecksumHeader] },
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListMultipartUploadsAsync(string bucket, string key, CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<string>>(async () =>
        {
            var uploadIds = new HashSet<string>(StringComparer.Ordinal);
            var markers = new HashSet<(string, string)>();
            var request = new ListMultipartUploadsRequest { BucketName = bucket, Prefix = key };
            while (true)
            {
                var response = await storage.ListMultipartUploadsAsync(request, cancellationToken);
                foreach (var upload in (response.MultipartUploads ?? []).Where(upload => upload.Key == key))
                    uploadIds.Add(RequireUploadId(upload.UploadId));

                if (response.IsTruncated != true) return uploadIds.ToArray();
                if (response.NextKeyMarker is not { Length: > 0 } marker
                    || response.NextUploadIdMarker is not { Length: > 0 } uploadMarker
                    || !markers.Add((marker, uploadMarker)))
                    throw new PhotoStorageUnavailableException();
                if (marker != key) return uploadIds.ToArray();
                request.KeyMarker = marker;
                request.UploadIdMarker = uploadMarker;
            }
        });

    public Task<string> CreateMultipartUploadAsync(string bucket, string key, string mediaType,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var response = await storage.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = bucket, Key = key, ContentType = mediaType, ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ChecksumType = ChecksumType.COMPOSITE
            }, cancellationToken);
            return RequireUploadId(response.UploadId);
        });

    public Task<IReadOnlyList<VideoPartModel>?> ListPartsAsync(string bucket, string key, string uploadId,
        CancellationToken cancellationToken) =>
        ExecuteForUploadAsync<IReadOnlyList<VideoPartModel>>(async () =>
        {
            var parts = new List<VideoPartModel>();
            var request = new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId };
            var lastNumber = 0;
            while (true)
            {
                var previousPageNumber = lastNumber;
                var response = await storage.ListPartsAsync(request, cancellationToken);
                if (response.BucketName != bucket || response.Key != key || response.UploadId != uploadId)
                    throw new PhotoStorageUnavailableException();

                foreach (var part in response.Parts ?? [])
                {
                    var listed = ReadPart(part);
                    if (listed.PartNumber <= lastNumber) throw new PhotoStorageUnavailableException();
                    parts.Add(listed);
                    lastNumber = listed.PartNumber;
                }

                if (response.IsTruncated != true) return parts;
                if (lastNumber <= previousPageNumber || response.NextPartNumberMarker != lastNumber)
                    throw new PhotoStorageUnavailableException();
                request.PartNumberMarker = lastNumber.ToString(CultureInfo.InvariantCulture);
            }
        });

    public Task<string?> CompleteMultipartUploadAsync(string bucket, string key, string uploadId,
        IReadOnlyList<VideoPartModel> parts, CancellationToken cancellationToken) =>
        ExecuteForUploadAsync(async () =>
        {
            var response = await storage.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = key, UploadId = uploadId,
                PartETags = parts.OrderBy(part => part.PartNumber).Select(part => new PartETag
                {
                    PartNumber = part.PartNumber, ETag = part.ETag, ChecksumSHA256 = Convert.ToBase64String(part.Sha256)
                }).ToList()
            }, cancellationToken);
            return RequireVersion(response.VersionId);
        });

    public Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken) =>
        ExecuteForUploadAsync(async () =>
        {
            await storage.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = bucket, Key = key, UploadId = uploadId
            }, cancellationToken);
            return string.Empty;
        });

    public Task<byte[]> ReadSha256Async(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken) =>
        S3VideoObjectReader.CopyAndHashAsync(storage, bucket, key, versionId, expectedSize, Stream.Null, cancellationToken);

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

    private GetPreSignedUrlRequest PutAuthorizationRequest(string bucket, string key, int size, byte[] sha256,
        DateTimeOffset expiresAt)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket, Key = key, Verb = HttpVerb.PUT, Protocol = protocol, Expires = expiresAt.UtcDateTime
        };
        request.Headers[ChecksumHeader] = Convert.ToBase64String(sha256);
        request.Headers["Content-Length"] = size.ToString(CultureInfo.InvariantCulture);
        return request;
    }

    private Task<PhotoUploadAuthorizationResponse> AuthorizePutAsync(GetPreSignedUrlRequest request,
        IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = await browserStorage.GetPreSignedURLAsync(request).WaitAsync(cancellationToken);
            if (request.UploadId is { } uploadId) url = EncodeUploadId(url, uploadId);
            return new PhotoUploadAuthorizationResponse("PUT", url, headers, new DateTimeOffset(request.Expires!.Value));
        });

    private static string EncodeUploadId(string url, string uploadId)
    {
        var queryStart = url.IndexOf('?') + 1;
        var raw = $"uploadId={uploadId}&";
        var encoded = $"uploadId={Uri.EscapeDataString(uploadId)}&";
        if (queryStart == 0) throw new PhotoStorageUnavailableException();
        var query = url.AsSpan(queryStart);
        if (query.StartsWith(encoded, StringComparison.Ordinal)) return url;
        if (!query.StartsWith(raw, StringComparison.Ordinal)) throw new PhotoStorageUnavailableException();
        return string.Concat(url.AsSpan(0, queryStart), encoded, query[raw.Length..]);
    }

    private static VideoPartModel ReadPart(PartDetail part)
    {
        var sha256 = new byte[32];
        if (part.PartNumber is not (> 0 and <= 10000) || part.Size is not > 0 || string.IsNullOrEmpty(part.ETag)
            || part.ChecksumSHA256 is not { } checksum
            || !Convert.TryFromBase64String(checksum, sha256, out var written) || written != sha256.Length)
            throw new PhotoStorageUnavailableException();
        return new VideoPartModel(part.PartNumber.Value, part.Size.Value, sha256, part.ETag);
    }

    private static string RequireUploadId(string? uploadId) => uploadId is { Length: > 0 }
        ? uploadId : throw new PhotoStorageUnavailableException();

    private static Task<T?> ExecuteForUploadAsync<T>(Func<Task<T>> operation) where T : class =>
        ExecuteAsync<T?>(async () =>
        {
            try
            {
                return await operation();
            }
            catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode == "NoSuchUpload")
            {
                return null;
            }
        });

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
