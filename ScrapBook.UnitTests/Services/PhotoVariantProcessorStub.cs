using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class PhotoVariantProcessorStub : IPhotoVariantProcessor
{
    public PhotoVariantSetModel Variants { get; set; } = new(2400, 1200,
        new([0xff, 0xd8, 1, 0xff, 0xd9], 2048, 1024), new([0xff, 0xd8, 2, 0xff, 0xd9], 400, 200));
    public List<(byte[] Original, PhotoPreparationProfileModel Profile, CancellationToken Token)> Calls { get; } = [];

    public Task<PhotoVariantSetModel> PrepareAsync(byte[] original, PhotoPreparationProfileModel profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((original, profile, cancellationToken));
        return Task.FromResult(Variants);
    }
}
