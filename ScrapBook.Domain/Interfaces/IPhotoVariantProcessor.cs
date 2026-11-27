using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IPhotoVariantProcessor
{
    Task<PhotoVariantSetModel> PrepareAsync(byte[] original, PhotoPreparationProfileModel profile, CancellationToken cancellationToken);
}
