using System.Security.Cryptography;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaVariantStorageStub : IMediaVariantStorage
{
    public byte[] Original { get; set; } = MediaPreparationTestFixture.Original;
    public Func<string, MediaStoredVariantModel, CancellationToken, Task<MediaStoredVariantModel>>? VerifyAsync { get; set; }
    public List<(MediaSourceEntity Source, CancellationToken Token)> Reads { get; } = [];
    public List<(string Bucket, string Key, PhotoVariantModel Variant, CancellationToken Token)> Stores { get; } = [];

    public Task<byte[]> ReadOriginalAsync(MediaSourceEntity source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((source, cancellationToken));
        return Task.FromResult(Original);
    }

    public Task<MediaStoredVariantModel> StoreVerifiedAsync(string bucket, string key, PhotoVariantModel variant, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stores.Add((bucket, key, variant, cancellationToken));
        var result = new MediaStoredVariantModel(bucket, key, key.EndsWith("/image.jpg", StringComparison.Ordinal) ? "image-version" : "thumb-version",
            variant.Bytes.Length, SHA256.HashData(variant.Bytes), MediaPreparationTestFixture.Confirmation.ConfirmedAt.AddHours(2));
        return VerifyAsync?.Invoke(key, result, cancellationToken) ?? Task.FromResult(result);
    }
}
