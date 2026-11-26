using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class VideoSubmissionDecoderStub(VideoMetadataModel metadata) : IVideoSubmissionDecoder
{
    public VideoMetadataModel Metadata { get; set; } = metadata;
    public List<(string Bucket, string Key, string Version, long ExpectedSize)> Reads { get; } = [];

    public Task<VideoMetadataModel> DecodeAsync(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((bucket, key, versionId, expectedSize));
        return Task.FromResult(Metadata);
    }
}
