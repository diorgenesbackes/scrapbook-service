using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaAudioStorage
{
    Task<MediaAudioInputModel> StageAsync(MediaSourceEntity source, string key, CancellationToken cancellationToken);
    Task<MediaAudioVariantModel> VerifyAsync(string bucket, string key, CancellationToken cancellationToken);
}
