using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class AudioSubmissionDecoderStub(AudioMetadataModel metadata) : IAudioSubmissionDecoder
{
    public AudioMetadataModel Metadata { get; set; } = metadata;
    public Exception? Error { get; set; }
    public List<(string Bucket, string Key, string Version, long Size)> Reads { get; } = [];

    public Task<AudioMetadataModel> DecodeAsync(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((bucket, key, versionId, expectedSize));
        return Error is { } error ? Task.FromException<AudioMetadataModel>(error) : Task.FromResult(Metadata);
    }
}
