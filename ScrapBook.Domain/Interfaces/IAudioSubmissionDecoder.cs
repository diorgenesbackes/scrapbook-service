using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IAudioSubmissionDecoder
{
    Task<AudioMetadataModel> DecodeAsync(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken);
}
