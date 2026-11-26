using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IPhotoSubmissionDecoder
{
    Task<PhotoImageModel> DecodeAsync(byte[] bytes, CancellationToken cancellationToken);
}
