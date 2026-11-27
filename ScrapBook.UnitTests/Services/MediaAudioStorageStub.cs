using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaAudioStorageStub : IMediaAudioStorage
{
    public Func<MediaSourceEntity, string, CancellationToken, Task<MediaAudioInputModel>> Stage { get; set; } =
        (_, key, _) => Task.FromResult(MediaAudioTestFixture.Input(key));

    public Func<string, string, CancellationToken, Task<MediaAudioVariantModel>> Verify { get; set; } =
        (_, key, _) => Task.FromResult(MediaAudioTestFixture.Variant(key));

    public List<(MediaSourceEntity Source, string Key, CancellationToken Token)> Staged { get; } = [];
    public List<(string Bucket, string Key, CancellationToken Token)> Verified { get; } = [];

    public Task<MediaAudioInputModel> StageAsync(MediaSourceEntity source, string key, CancellationToken cancellationToken)
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
