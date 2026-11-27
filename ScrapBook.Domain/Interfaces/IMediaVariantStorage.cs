using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaVariantStorage
{
    Task<byte[]> ReadOriginalAsync(MediaSourceEntity source, CancellationToken cancellationToken);
    Task<MediaStoredVariantModel> StoreVerifiedAsync(string bucket, string key, PhotoVariantModel variant,
        CancellationToken cancellationToken);
}
