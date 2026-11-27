using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace ScrapBook.UnitTests.Storage;

internal sealed class MediaAudioS3ClientStub(string sourceKey, byte[] sourceBytes)
    : AmazonS3Client("test-access", "test-secret", RegionEndpoint.USEast1)
{
    public string SourceVersion { get; set; } = "confirmed-version";
    public string WrittenVersion { get; set; } = "staged-version";
    public string? HeadVersion { get; set; }
    public string? ReadVersion { get; set; }
    public bool ConflictOnPut { get; set; }
    public byte[]? ExistingBytes { get; set; }
    public string? ExistingSourceHash { get; set; }
    public string SourceMediaType { get; set; } = "audio/mp4";
    public List<(PutObjectRequest Request, byte[] Bytes, CancellationToken Token)> Puts { get; } = [];
    public List<(GetObjectMetadataRequest Request, CancellationToken Token)> Heads { get; } = [];
    public List<(GetObjectRequest Request, CancellationToken Token)> Reads { get; } = [];
    private readonly DateTime lastModified = new(2026, 11, 27, 14, 0, 5, DateTimeKind.Utc);

    public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await request.InputStream.CopyToAsync(buffer, cancellationToken);
        Puts.Add((request, buffer.ToArray(), cancellationToken));
        if (ConflictOnPut)
            throw new AmazonS3Exception("Already exists") { StatusCode = HttpStatusCode.PreconditionFailed };

        return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK, VersionId = WrittenVersion };
    }

    public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Heads.Add((request, cancellationToken));
        var (bytes, version, mediaType, metadata) = ReadObject(request.Key);
        var response = new GetObjectMetadataResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
            ContentLength = bytes.Length,
            VersionId = HeadVersion ?? version,
            LastModified = lastModified,
            ContentType = mediaType,
            ChecksumSHA256 = Convert.ToBase64String(SHA256.HashData(bytes))
        };
        response.Headers.ContentType = mediaType;
        SetMetadata(response.Metadata, metadata);
        return Task.FromResult(response);
    }

    public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((request, cancellationToken));
        var (bytes, version, mediaType, metadata) = ReadObject(request.Key);
        var response = new GetObjectResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
            ContentLength = bytes.Length,
            VersionId = ReadVersion ?? version,
            LastModified = lastModified,
            ResponseStream = new MemoryStream(bytes),
            ChecksumSHA256 = Convert.ToBase64String(SHA256.HashData(bytes))
        };
        response.Headers.ContentType = mediaType;
        SetMetadata(response.Metadata, metadata);
        return Task.FromResult(response);
    }

    private (byte[] Bytes, string Version, string MediaType, MetadataCollection? Metadata) ReadObject(string key)
    {
        if (key == sourceKey) return (sourceBytes, SourceVersion, SourceMediaType, null);
        if (Puts.LastOrDefault() is var (write, bytes, _) && write is not null && write.Key == key)
            return (ExistingBytes ?? bytes, WrittenVersion, write.ContentType, write.Metadata);

        throw new AmazonS3Exception("Missing object") { StatusCode = HttpStatusCode.NotFound };
    }

    private void SetMetadata(MetadataCollection target, MetadataCollection? original)
    {
        if (original is null) return;
        foreach (var key in original.Keys)
            target[key] = original[key];
        if (ExistingSourceHash is not null)
            target["source-sha256"] = ExistingSourceHash;
    }
}
