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

    public async Task<bool> DeleteExpiredVersionsAsync(string bucket, string key, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        try
        {
            var (versions, complete) = await ListExactVersionsAsync(bucket, key, cancellationToken);
            var expired = versions.Where(version => IsEligible(version, cutoff)).ToArray();
            foreach (var version in expired)
            {
                var deletion = await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = version.VersionId
                }, cancellationToken);
                if (deletion.HttpStatusCode != HttpStatusCode.NoContent) throw new MediaVariantStorageException();
            }

            return complete && expired.Length == versions.Count;
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            throw new MediaVariantStorageException(error);
        }
    }

    private async Task<(List<S3ObjectVersion> Versions, bool Complete)> ListExactVersionsAsync(string bucket, string key,
        CancellationToken cancellationToken)
    {
        var request = new ListVersionsRequest { BucketName = bucket, Prefix = key, MaxKeys = PageSize };
        var versions = new List<S3ObjectVersion>();
        for (var index = 0; index < MaximumPages; index++)
        {
            var page = await client.ListVersionsAsync(request, cancellationToken);
            if (page.HttpStatusCode != HttpStatusCode.OK) throw new MediaVariantStorageException();
            versions.AddRange((page.Versions ?? []).Where(version => version.Key == key && version.IsDeleteMarker != true));
            if (page.IsTruncated != true || string.CompareOrdinal(page.NextKeyMarker, key) > 0) return (versions, true);
            if (page.NextKeyMarker != key || string.IsNullOrEmpty(page.NextVersionIdMarker)
                || page.NextVersionIdMarker == request.VersionIdMarker) break;

            request.KeyMarker = page.NextKeyMarker;
            request.VersionIdMarker = page.NextVersionIdMarker;
        }
        return (versions, false);
    }

    private static bool IsEligible(S3ObjectVersion version, DateTimeOffset cutoff) =>
        version.VersionId is { Length: > 0 } and not "null" && version.LastModified is { } lastModified
        && lastModified != default && new DateTimeOffset(lastModified.ToUniversalTime()) <= cutoff;
}
