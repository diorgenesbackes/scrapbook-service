using System.Text.Json;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Decoders;

namespace ScrapBook.Api.Workers;

public static class PhotoDecoderRunner
{
    public static async Task<int> RunAsync(int length, CancellationToken cancellationToken)
    {
        if (length is < 1 or > 20_000_000) return ProcessPhotoSubmissionDecoder.InvalidContentExitCode;
        try
        {
            var bytes = new byte[length];
            await using var input = Console.OpenStandardInput();
            await input.ReadExactlyAsync(bytes, cancellationToken);
            var result = MagickPhotoDecoder.Decode(bytes);
            await Console.Out.WriteAsync(JsonSerializer.Serialize(result).AsMemory(), cancellationToken);
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
    }
}
