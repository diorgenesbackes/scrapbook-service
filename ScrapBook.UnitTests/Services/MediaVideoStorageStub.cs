using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaVideoStorageStub : IMediaVideoStorage
{
    public Func<MediaSourceEntity, string, CancellationToken, Task<MediaVideoInputModel>> Stage { get; set; } =
        (_, key, _) => Task.FromResult(MediaVideoTestFixture.Input(key));

    public Func<string, string, CancellationToken, Task<MediaAudioVariantModel>> Verify { get; set; } =
        (_, key, _) => Task.FromResult(MediaVideoTestFixture.Variant(key));

    public List<(MediaSourceEntity Source, string Key, CancellationToken Token)> Staged { get; } = [];
    public List<(string Bucket, string Key, CancellationToken Token)> Verified { get; } = [];

    public Task<MediaVideoInputModel> StageAsync(MediaSourceEntity source, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Staged.Add((source, key, cancellationToken));
        return Stage(source, key, cancellationToken);
    }

    public Task<MediaAudioVariantModel> VerifyAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Verified.Add((bucket, key, cancellationToken));
        return Verify(bucket, key, cancellationToken);
    }
}
