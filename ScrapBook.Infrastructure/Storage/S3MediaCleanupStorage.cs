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

    public async Task<bool> DeleteExpiredVersionsAsync(string bucket, string key, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket,
                Prefix = key,
                MaxKeys = PageSize
            }, cancellationToken);
            if (page.HttpStatusCode != HttpStatusCode.OK) throw new MediaVariantStorageException();
            var versions = (page.Versions ?? []).Where(version => version.Key == key && version.IsDeleteMarker != true).ToArray();
            foreach (var version in versions)
            {
                if (!IsEligible(version, cutoff)) continue;
                var deletion = await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = version.VersionId
                }, cancellationToken);
                if (deletion.HttpStatusCode != HttpStatusCode.NoContent) throw new MediaVariantStorageException();
            }

            return versions.All(version => IsEligible(version, cutoff))
                && (page.IsTruncated != true || page.NextKeyMarker != key);
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            throw new MediaVariantStorageException(error);
        }
    }

    private static bool IsEligible(S3ObjectVersion version, DateTimeOffset cutoff) =>
        version.VersionId is { Length: > 0 } and not "null" && version.LastModified is { } lastModified
        && lastModified != default && new DateTimeOffset(lastModified.ToUniversalTime()) <= cutoff;
}
