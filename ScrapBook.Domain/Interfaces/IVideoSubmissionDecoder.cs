using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IVideoSubmissionDecoder
{
    Task<VideoMetadataModel> DecodeAsync(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken);
}
