using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Infrastructure.Storage;

public sealed class S3MediaCleanupStorage(IAmazonS3 client) : IMediaCleanupStorage
{
    private const int PageSize = 16;
    private const int MaximumPages = 4;

    public async Task<string?> FindExpiredVersionAsync(string bucket, string key, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        try
        {
            var version = await FindExactVersionAsync(bucket, key, cancellationToken);
            return version is { LastModified: { } modified, VersionId: { } versionId } && modified != default
                && new DateTimeOffset(modified.ToUniversalTime()) <= cutoff && !string.IsNullOrWhiteSpace(versionId)
                && versionId != "null" ? versionId : null;
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            throw new MediaVariantStorageException(error);
        }
    }

    public async Task DeleteVersionAsync(string bucket, string key, string versionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId == "null") throw new MediaVariantStorageException();
        try
        {
            var deletion = await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId
            }, cancellationToken);
            if (deletion.HttpStatusCode != HttpStatusCode.NoContent) throw new MediaVariantStorageException();
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            throw new MediaVariantStorageException(error);
        }
    }

    private async Task<S3ObjectVersion?> FindExactVersionAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        var request = new ListVersionsRequest { BucketName = bucket, Prefix = key, MaxKeys = PageSize };
        S3ObjectVersion? match = null;
        for (var index = 0; index < MaximumPages; index++)
        {
            var page = await client.ListVersionsAsync(request, cancellationToken);
            if (page.HttpStatusCode != HttpStatusCode.OK) throw new MediaVariantStorageException();
            foreach (var version in page.Versions ?? [])
            {
                if (version.Key != key || version.IsDeleteMarker == true) continue;
                if (match is not null) return null;
                match = version;
            }

            if (page.IsTruncated != true || string.CompareOrdinal(page.NextKeyMarker, key) > 0) return match;
            if (page.NextKeyMarker != key || string.IsNullOrEmpty(page.NextVersionIdMarker)
                || page.NextVersionIdMarker == request.VersionIdMarker) break;

            request.KeyMarker = page.NextKeyMarker;
            request.VersionIdMarker = page.NextVersionIdMarker;
        }
        return null;
    }
}
