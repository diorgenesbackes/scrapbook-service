using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.S3;
using ScrapBook.Infrastructure.Storage;

namespace ScrapBook.UnitTests.Storage;

public sealed class S3PhotoSubmissionStorageTests
{
    [Fact]
    public async Task MultipartAuthorizationBindsKeyUploadNumberSizeAndChecksumAsync()
    {
        using var client = new AmazonS3Client("test-access", "test-secret", RegionEndpoint.USEast1);
        using var storage = new S3PhotoSubmissionStorage(client, client);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(3);
        var checksum = SHA256.HashData("video part"u8.ToArray());
        const string key = "submissions/temporary/example/0";
        const string uploadId = "provider/id+with=symbols";
        var grant = await storage.AuthorizePartAsync("test-uploads", key, uploadId, 7, 156, checksum,
            expiresAt, CancellationToken.None);
        var url = new Uri(grant.Url);
        var query = ParseQuery(url);

        Assert.Equal("PUT", grant.Method);
        Assert.Equal(expiresAt, grant.ExpiresAt);
        Assert.Equal(Convert.ToBase64String(checksum), Assert.Single(grant.Headers).Value);
        Assert.Equal("/" + key, url.AbsolutePath);
        Assert.Equal("7", query["partNumber"]);
        Assert.Equal(uploadId, query["uploadId"]);
        Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
        var signedHeaders = query["X-Amz-SignedHeaders"].Split(';');
        Assert.Contains("content-length", signedHeaders);
        Assert.Contains("x-amz-checksum-sha256", signedHeaders);
    }

    private static Dictionary<string, string> ParseQuery(Uri url) =>
        url.Query.TrimStart('?').Split('&').Select(item => item.Split('=', 2)).ToDictionary(
            parts => WebUtility.UrlDecode(parts[0]), parts => WebUtility.UrlDecode(parts[1]), StringComparer.Ordinal);
}
