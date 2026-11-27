using Amazon.S3;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Decoders;

namespace ScrapBook.Infrastructure.Storage;

public sealed class S3MediaVideoStorage(IAmazonS3 client, VideoTranscodingDecoder decoder) : IMediaVideoStorage
{
    private const long MaximumSourceBytes = 500_000_000;
    private const long MaximumPlaybackBytes = 200_000_000;
    private readonly S3MediaTranscodingStorage storage = new(client);

    public Task<MediaVideoInputModel> StageAsync(MediaSourceEntity source, string key, CancellationToken cancellationToken) =>
        S3MediaTranscodingStorage.InScratchAsync("video", async (path, token) =>
        {
            S3MediaTranscodingStorage.RequireKey(key, "transcode-input", "input", source.FileId);
            var originalHash = await storage.ReadOriginalAsync(source, path, MaximumSourceBytes, token);
            var input = await decoder.PrepareAsync(path, token);
            var written = await storage.StageFileAsync(source, key, input.MediaType, path, input.Path, originalHash,
                VideoTranscodingDecoder.MaximumInputBytes, token);
            return new MediaVideoInputModel(written, input.Properties);
        }, cancellationToken);

    public Task<MediaAudioVariantModel> VerifyAsync(string bucket, string key, CancellationToken cancellationToken) =>
        S3MediaTranscodingStorage.InScratchAsync("video", async (path, token) =>
        {
            S3MediaTranscodingStorage.RequireKey(key, "transcode-output", "playback.mp4", null);
            var metadata = await storage.ReadMetadataAsync(bucket, key, null, token);
            if (metadata.ContentLength is <= 0 or > MaximumPlaybackBytes) throw new MediaOutputInvalidException();
            var hash = await storage.DownloadAsync(bucket, key, metadata.VersionId, metadata.ContentLength, path, token);

            var video = await decoder.InspectPlaybackAsync(path, token);
            var stored = S3MediaTranscodingStorage.StoredVariant(bucket, key, metadata, hash);
            return new MediaAudioVariantModel(stored, video.Duration, "video/mp4", video.Container, video.AudioCodec,
                video.AudioChannels, video.AudioSampleRate, video);
        }, cancellationToken);
}
