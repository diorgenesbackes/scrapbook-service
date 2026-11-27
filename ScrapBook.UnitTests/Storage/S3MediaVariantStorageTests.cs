using System.Security.Cryptography;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Storage;
using ScrapBook.UnitTests.Services;

namespace ScrapBook.UnitTests.Storage;

public sealed class S3MediaVariantStorageTests
{
    private const string Bucket = "private-media";
    private const string Key = "submissions/derived/701/attempt/thumbnail.jpg";
    private static readonly PhotoVariantModel Variant = new([0xff, 0xd8, 1, 0xff, 0xd9], 32, 32);

    [Fact]
    public async Task PinsTheReadToTheConfirmedOriginalVersionAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var source = MediaPreparationTestFixture.Source();
        using var client = new MediaVariantS3ClientStub
        {
            StoredBytes = MediaPreparationTestFixture.Original,
            ReturnedVersion = source.VersionId
        };
        using var originals = new S3PhotoSubmissionStorage(client, client);
        var storage = new S3MediaVariantStorage(client, originals);

        Assert.Equal(MediaPreparationTestFixture.Original, await storage.ReadOriginalAsync(source, cancellation.Token));

        var (read, token) = Assert.Single(client.Reads);
        Assert.Equal((source.Bucket, source.ObjectKey, source.VersionId), (read.BucketName, read.Key, read.VersionId));
        Assert.Equal(cancellation.Token, token);
        Assert.Empty(client.Puts);
        Assert.Empty(client.Heads);
    }

    [Fact]
    public async Task WritesWithoutOverwriteAndVerifiesTheExactNewVersionAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new MediaVariantS3ClientStub { StoredBytes = Variant.Bytes };
        using var originals = new S3PhotoSubmissionStorage(client, client);
        var storage = new S3MediaVariantStorage(client, originals);

        var result = await storage.StoreVerifiedAsync(Bucket, Key, Variant, cancellation.Token);

        var hash = SHA256.HashData(Variant.Bytes);
        Assert.Equal((Bucket, Key, client.WrittenVersion, (long)Variant.Bytes.Length), (result.Bucket, result.Key, result.VersionId, result.Size));
        Assert.Equal(hash, result.Sha256);
        var (put, bytes, putToken) = Assert.Single(client.Puts);
        Assert.Equal((Bucket, Key, "image/jpeg", "*", Convert.ToBase64String(hash)),
            (put.BucketName, put.Key, put.ContentType, put.IfNoneMatch, put.ChecksumSHA256));
        Assert.Equal(cancellation.Token, putToken);
        Assert.Equal(Variant.Bytes, bytes);
        var (head, headToken) = Assert.Single(client.Heads);
        var (read, readToken) = Assert.Single(client.Reads);
        Assert.Equal((Bucket, Key, client.WrittenVersion, cancellation.Token), (head.BucketName, head.Key, head.VersionId, headToken));
        Assert.Equal((Bucket, Key, client.WrittenVersion, cancellation.Token), (read.BucketName, read.Key, read.VersionId, readToken));
    }

    [Fact]
    public async Task RejectsTamperedBytesEvenWhenSizeAndVersionMatchAsync()
    {
        using var client = new MediaVariantS3ClientStub { StoredBytes = [0xff, 0xd8, 2, 0xff, 0xd9] };
        using var originals = new S3PhotoSubmissionStorage(client, client);
        var storage = new S3MediaVariantStorage(client, originals);

        await Assert.ThrowsAsync<MediaOutputInvalidException>(() => storage.StoreVerifiedAsync(Bucket, Key, Variant, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAnUnversionedWriteBeforeRequestingMetadataAsync()
    {
        using var client = new MediaVariantS3ClientStub { StoredBytes = Variant.Bytes, WrittenVersion = "null" };
        using var originals = new S3PhotoSubmissionStorage(client, client);
        var storage = new S3MediaVariantStorage(client, originals);

        await Assert.ThrowsAsync<MediaVariantStorageException>(() => storage.StoreVerifiedAsync(Bucket, Key, Variant, CancellationToken.None));

        Assert.Empty(client.Heads);
        Assert.Empty(client.Reads);
    }
}
