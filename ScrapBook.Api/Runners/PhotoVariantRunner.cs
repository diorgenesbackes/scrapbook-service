using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Decoders;

namespace ScrapBook.Api.Runners;

public static class PhotoVariantRunner
{
    public static async Task<int> RunAsync(int length, PhotoPreparationProfileModel profile, CancellationToken cancellationToken)
    {
        if (length is < 1 or > 20_000_000 || profile is not { ImageMaximumSide: 2048, ThumbnailMaximumSide: 400, Quality: 85 })
        {
            return ProcessPhotoSubmissionDecoder.InvalidContentExitCode;
        }
        try
        {
            var bytes = new byte[length];
            await using var input = Console.OpenStandardInput();
            await input.ReadExactlyAsync(bytes, cancellationToken);
            var result = MagickPhotoDecoder.Prepare(bytes, profile);
            await using var output = Console.OpenStandardOutput();
            await PhotoVariantCodec.WriteAsync(output, result, cancellationToken);
            return 0;
        }
        catch (PhotoContentInvalidException)
        {
            return ProcessPhotoSubmissionDecoder.InvalidContentExitCode;
        }
        catch (PhotoDecoderBudgetException)
        {
            return ProcessPhotoSubmissionDecoder.BudgetExceededExitCode;
        }
        catch (MediaOutputInvalidException)
        {
            return ProcessPhotoSubmissionDecoder.InvalidOutputExitCode;
        }
    }
}
