using System.Globalization;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

public sealed class ProcessPhotoVariantProcessor : IPhotoVariantProcessor
{
    public const string Argument = "--prepare-private-photo";

    public Task<PhotoVariantSetModel> PrepareAsync(byte[] original, PhotoPreparationProfileModel profile,
        CancellationToken cancellationToken) =>
        ProcessPhotoSubmissionDecoder.ExecuteAsync(original,
            [Argument, Format(profile.ImageMaximumSide), Format(profile.ThumbnailMaximumSide), Format(profile.Quality)],
            (process, token) => PhotoVariantCodec.ReadAsync(process.StandardOutput.BaseStream, token), cancellationToken);

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
