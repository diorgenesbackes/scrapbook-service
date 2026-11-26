using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class PhotoSubmissionDecoderStub : IPhotoSubmissionDecoder
{
    public PhotoImageModel Image { get; set; } = new("image/png", "png", 8, 6, null);
    public Exception? Error { get; set; }
    public int Calls { get; private set; }

    public Task<PhotoImageModel> DecodeAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ++Calls;
        return Error is null ? Task.FromResult(Image) : Task.FromException<PhotoImageModel>(Error);
    }
}
