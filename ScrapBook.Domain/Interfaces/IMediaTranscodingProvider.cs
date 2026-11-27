using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaTranscodingProvider
{
    Task<MediaProviderJobModel> CreateAsync(MediaAudioRequestModel request, CancellationToken cancellationToken);
    Task<MediaProviderJobModel?> GetAsync(string jobId, CancellationToken cancellationToken);
    Task<MediaProviderJobModel?> FindAsync(MediaAudioRequestModel request, DateTimeOffset submittedAt,
        CancellationToken cancellationToken);
}
