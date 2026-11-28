using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace ScrapBook.UnitTests.Storage;

internal sealed class MediaCleanupS3ClientStub() : AmazonS3Client("test-access", "test-secret", RegionEndpoint.USEast1)
{
    public ListVersionsResponse Page { get; set; } = new() { HttpStatusCode = HttpStatusCode.OK };
    public Dictionary<string, ListVersionsResponse> FollowingPages { get; } = [];
    public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;
    public Exception? ListFailure { get; set; }
    public List<(ListVersionsRequest Request, CancellationToken Token)> Lists { get; } = [];
    public List<(DeleteObjectRequest Request, CancellationToken Token)> Deletes { get; } = [];

    public override Task<ListVersionsResponse> ListVersionsAsync(ListVersionsRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lists.Add((new ListVersionsRequest
        {
            BucketName = request.BucketName,
            Prefix = request.Prefix,
            MaxKeys = request.MaxKeys,
            KeyMarker = request.KeyMarker,
            VersionIdMarker = request.VersionIdMarker
        }, cancellationToken));
        if (ListFailure is { } failure) return Task.FromException<ListVersionsResponse>(failure);
        return Task.FromResult(request.VersionIdMarker is { } marker ? FollowingPages[marker] : Page);
    }

    public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deletes.Add((request, cancellationToken));
        return Task.FromResult(new DeleteObjectResponse { HttpStatusCode = DeleteStatus });
    }
}
