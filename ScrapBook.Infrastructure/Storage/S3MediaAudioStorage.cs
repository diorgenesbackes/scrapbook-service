using Amazon.S3;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Decoders;

namespace ScrapBook.Infrastructure.Storage;

public sealed class S3MediaAudioStorage(IAmazonS3 client, AudioTranscodingDecoder decoder) : IMediaAudioStorage, IDisposable
{
    private const long MaximumSourceBytes = 50_000_000;
    private const long MaximumInputBytes = 64_000_000;
    private const long MaximumPlaybackBytes = 10_000_000;
    private readonly SemaphoreSlim operationSlots = new(2, 2);
    private readonly S3MediaTranscodingStorage storage = new(client);

    public Task<MediaAudioInputModel> StageAsync(MediaSourceEntity source, string key, CancellationToken cancellationToken) =>
        InScratchAsync(async (path, token) =>
        {
            S3MediaTranscodingStorage.RequireKey(key, "transcode-input", "input", source.FileId);
            var originalHash = await storage.ReadOriginalAsync(source, path, MaximumSourceBytes, token);
            var input = await decoder.PrepareAsync(path, token);
            var written = await storage.StageFileAsync(source, key, input.MediaType, path, input.Path, originalHash,
                MaximumInputBytes, token);
            return new MediaAudioInputModel(written, input.Duration, input.Channels);
        }, cancellationToken);

    public Task<MediaAudioVariantModel> VerifyAsync(string bucket, string key, CancellationToken cancellationToken) =>
        InScratchAsync(async (path, token) =>
        {
            S3MediaTranscodingStorage.RequireKey(key, "transcode-output", "playback.m4a", null);
            var metadata = await storage.ReadMetadataAsync(bucket, key, null, token);
            if (metadata.ContentLength is <= 0 or > MaximumPlaybackBytes) throw new MediaOutputInvalidException();
            var hash = await storage.DownloadAsync(bucket, key, metadata.VersionId, metadata.ContentLength, path, token);
            var audio = await decoder.InspectPlaybackAsync(path, token);
            var stored = S3MediaTranscodingStorage.StoredVariant(bucket, key, metadata, hash);
            return new MediaAudioVariantModel(stored, audio.Primary.Duration(false), audio.MediaType, audio.Container,
                audio.Primary.Codec, audio.Primary.Channels, audio.Primary.SampleRate);
        }, cancellationToken);

    public void Dispose() => operationSlots.Dispose();

    private async Task<TResult> InScratchAsync<TResult>(Func<string, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        await operationSlots.WaitAsync(cancellationToken);
        try
        {
            return await S3MediaTranscodingStorage.InScratchAsync("audio", operation, cancellationToken);
        }
        finally
        {
            operationSlots.Release();
        }
    }
}
