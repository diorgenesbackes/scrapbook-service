using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace ScrapBook.UnitTests.Storage;

internal sealed class MediaVariantS3ClientStub() : AmazonS3Client("test-access", "test-secret", RegionEndpoint.USEast1)
{
    public byte[] StoredBytes { get; set; } = [];
    public string WrittenVersion { get; set; } = "written-version";
    public string ReturnedVersion { get; set; } = "written-version";
    public List<(PutObjectRequest Request, byte[] Bytes, CancellationToken Token)> Puts { get; } = [];
    public List<(GetObjectMetadataRequest Request, CancellationToken Token)> Heads { get; } = [];
    public List<(GetObjectRequest Request, CancellationToken Token)> Reads { get; } = [];

    public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await request.InputStream.CopyToAsync(stream, cancellationToken);
        Puts.Add((request, stream.ToArray(), cancellationToken));
        return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK, VersionId = WrittenVersion };
    }

    public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Heads.Add((request, cancellationToken));
        var metadata = new GetObjectMetadataResponse
        {
            VersionId = ReturnedVersion,
            LastModified = DateTime.UtcNow,
            ContentLength = StoredBytes.Length
        };
        return Task.FromResult(metadata);
    }

    public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((request, cancellationToken));
        return Task.FromResult(new GetObjectResponse
        {
            VersionId = ReturnedVersion,
            ContentLength = StoredBytes.Length,
            ResponseStream = new MemoryStream(StoredBytes)
        });
    }
}
