using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Storage;

namespace ScrapBook.UnitTests.Storage;

public sealed class S3MediaCleanupStorageTests
{
    private const string Bucket = "private-media";
    private const string Key = "submissions/derived/701/47a30fd4c9314fbb84b6440cd832b9f2/image.jpg";
    private static readonly DateTimeOffset Cutoff = new(2026, 11, 27, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FindsOneExactVersionAtTheInclusiveCutoffAndDeletesItOnlyWhenExplicitlyRequestedAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new MediaCleanupS3ClientStub();
        client.Page.Versions =
        [
            Version(Key, "at-cutoff", Cutoff),
            Version(Key + ".other", "neighbor", Cutoff.AddDays(-3)),
            Version(Key, "tombstone", Cutoff.AddDays(-3), deleteMarker: true)
        ];
        var storage = new S3MediaCleanupStorage(client);

        var found = await storage.FindExpiredVersionAsync(Bucket, Key, Cutoff, cancellation.Token);

        Assert.NotNull(found);
        Assert.Equal("at-cutoff", found);
        var (list, token) = Assert.Single(client.Lists);
        Assert.Equal((Bucket, Key, cancellation.Token), (list.BucketName, list.Prefix, token));
        Assert.InRange(list.MaxKeys.GetValueOrDefault(), 1, 100);
        Assert.Empty(client.Deletes);

        await storage.DeleteVersionAsync(Bucket, Key, found, cancellation.Token);

        AssertDeletion(Assert.Single(client.Deletes), "at-cutoff", cancellation.Token);
        Assert.Single(client.Lists);
    }

    [Theory]
    [InlineData("old", -1, "old")]
    [InlineData("new", 1, null)]
    [InlineData("null", -1, null)]
    [InlineData("", -1, null)]
    [InlineData(" ", -1, null)]
    [InlineData("unknown-age", null, null)]
    public async Task RequiresBothAnExplicitVersionAndProvenAgeAsync(string version, int? offset, string? expected)
    {
        using var client = new MediaCleanupS3ClientStub();
        client.Page.Versions = [Version(Key, version, offset is int seconds ? Cutoff.AddSeconds(seconds) : null)];

        Assert.Equal(expected, await new S3MediaCleanupStorage(client).FindExpiredVersionAsync(Bucket, Key, Cutoff, CancellationToken.None));

        Assert.Empty(client.Deletes);
    }

    [Fact]
    public async Task RefusesToSelectBetweenTwoConcreteVersionsEvenWhenOnlyOneIsOldAsync()
    {
        using var client = new MediaCleanupS3ClientStub();
        client.Page.Versions = [Version(Key, "newer", Cutoff.AddSeconds(1)), Version(Key, "older", Cutoff.AddSeconds(-1))];

        Assert.Null(await new S3MediaCleanupStorage(client).FindExpiredVersionAsync(Bucket, Key, Cutoff, CancellationToken.None));

        Assert.Empty(client.Deletes);
    }

    [Theory]
    [InlineData(false, null, "old")]
    [InlineData(true, Key, null)]
    [InlineData(true, Key + ".other", "old")]
    public async Task RequiresACompleteListingOfTheExactKeyAsync(bool truncated, string? nextKey, string? expected)
    {
        using var client = new MediaCleanupS3ClientStub();
        client.Page.IsTruncated = truncated;
        client.Page.NextKeyMarker = nextKey;
        client.Page.Versions = [Version(Key, "old", Cutoff.AddDays(-1))];

        Assert.Equal(expected, await new S3MediaCleanupStorage(client).FindExpiredVersionAsync(Bucket, Key, Cutoff, CancellationToken.None));

        Assert.Empty(client.Deletes);
    }

    [Fact]
    public async Task FollowsAnExactKeyVersionCursorPastDeleteMarkersAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new MediaCleanupS3ClientStub();
        client.Page.IsTruncated = true;
        client.Page.NextKeyMarker = Key;
        client.Page.NextVersionIdMarker = "marker";
        client.Page.Versions = [Version(Key, "marker", Cutoff, deleteMarker: true)];
        client.FollowingPages.Add("marker", new()
        {
            HttpStatusCode = HttpStatusCode.OK,
            Versions = [Version(Key, "older-version", Cutoff.AddDays(-1))]
        });

        Assert.Equal("older-version", await new S3MediaCleanupStorage(client)
            .FindExpiredVersionAsync(Bucket, Key, Cutoff, cancellation.Token));

        Assert.Collection(client.Lists,
            item => Assert.Equal((null, null, cancellation.Token), (item.Request.KeyMarker, item.Request.VersionIdMarker, item.Token)),
            item => Assert.Equal((Key, "marker", cancellation.Token), (item.Request.KeyMarker, item.Request.VersionIdMarker, item.Token)));
        Assert.Empty(client.Deletes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RejectsUnversionedDeletesBeforeCallingS3Async(string version)
    {
        using var client = new MediaCleanupS3ClientStub();

        await Assert.ThrowsAsync<MediaVariantStorageException>(
            () => new S3MediaCleanupStorage(client).DeleteVersionAsync(Bucket, Key, version, CancellationToken.None));

        Assert.Empty(client.Deletes);
    }

    [Fact]
    public async Task TranslatesAnS3FailureWhenListingVersionsAsync()
    {
        using var client = new MediaCleanupS3ClientStub { ListFailure = new AmazonS3Exception("storage unavailable") };

        var error = await Assert.ThrowsAsync<MediaVariantStorageException>(
            () => new S3MediaCleanupStorage(client).FindExpiredVersionAsync(Bucket, Key, Cutoff, CancellationToken.None));

        Assert.Same(client.ListFailure, error.InnerException);
        Assert.Empty(client.Deletes);
    }

    [Fact]
    public async Task ReportsWhenS3CannotConfirmTheExplicitDeletionAsync()
    {
        using var client = new MediaCleanupS3ClientStub { DeleteStatus = HttpStatusCode.ServiceUnavailable };

        await Assert.ThrowsAsync<MediaVariantStorageException>(
            () => new S3MediaCleanupStorage(client).DeleteVersionAsync(Bucket, Key, "pinned-version", CancellationToken.None));

        AssertDeletion(Assert.Single(client.Deletes), "pinned-version", CancellationToken.None);
    }

    private static S3ObjectVersion Version(string key, string id, DateTimeOffset? modifiedAt, bool deleteMarker = false) =>
        new() { Key = key, VersionId = id, LastModified = modifiedAt?.UtcDateTime, IsDeleteMarker = deleteMarker };

    private static void AssertDeletion((DeleteObjectRequest Request, CancellationToken Token) item, string version,
        CancellationToken token) =>
        Assert.Equal((Bucket, Key, version, token), (item.Request.BucketName, item.Request.Key, item.Request.VersionId, item.Token));
}
