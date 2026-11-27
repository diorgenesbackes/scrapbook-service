using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

internal static class MediaVideoProfileService
{
    internal static MediaVideoProfileModel? Create(MediaVideoPropertiesModel? source)
    {
        if (source is not
            {
                Container: "mp4" or "mov" or "webm", VideoCodec.Length: > 0,
                Width: > 0, Height: > 0, PixelAspectNumerator: > 0, PixelAspectDenominator: > 0,
                Duration: > 0 and <= 300, FrameRateNumerator: > 0, FrameRateDenominator: > 0,
                Rotation: 0 or 90 or -90 or 180 or -180 or 270, AudioChannels: >= 0
            }
            || (source.AudioChannels == 0 ? source.AudioCodec is not null : string.IsNullOrWhiteSpace(source.AudioCodec)))
        {
            return null;
        }

        var width = (decimal)source.Width * source.PixelAspectNumerator;
        var height = (decimal)source.Height * source.PixelAspectDenominator;
        if (source.Rotation is 90 or -90 or 270)
        {
            (width, height) = (height, width);
        }
        var maxWidth = width > height ? 1920 : 1080;
        var maxHeight = width < height ? 1920 : 1080;
        var (fittedWidth, fittedHeight) = Fit(width, height, source.PixelAspectDenominator, maxWidth, maxHeight);
        if (fittedWidth == 0 || fittedHeight == 0) return null;

        var (numerator, denominator) = LimitFrameRate(source.FrameRateNumerator, source.FrameRateDenominator);
        return new(fittedWidth, fittedHeight, numerator, denominator, source.Rotation);
    }

    internal static bool MatchesOutput(MediaAudioVariantModel variant, MediaVideoPropertiesModel source,
        MediaVideoProfileModel profile, MediaAudioProfileModel audio)
    {
        if (variant.Video is not
            {
                Container: "mp4", VideoCodec: "h264", Rotation: 0, VariableFrameRate: false, HasLocationMetadata: false,
                PixelAspectNumerator: > 0, FrameRateNumerator: > 0, FrameRateDenominator: > 0, Duration: > 0
            } video || variant.MediaType != "video/mp4" || variant.Container != video.Container
            || variant.Duration != video.Duration || video.Width != profile.Width || video.Height != profile.Height
            || video.PixelAspectNumerator != video.PixelAspectDenominator
            || (long)video.FrameRateNumerator * profile.FrameRateDenominator
                != (long)profile.FrameRateNumerator * video.FrameRateDenominator)
        {
            return false;
        }

        var frameDuration = (decimal)profile.FrameRateDenominator / profile.FrameRateNumerator;
        var tolerance = Math.Max(0.250m, Math.Min(frameDuration, source.Duration / 2));
        return Math.Abs(video.Duration - source.Duration) <= tolerance
            && variant.AudioCodec == video.AudioCodec && variant.Channels == video.AudioChannels
            && variant.SampleRate == video.AudioSampleRate && video.AudioChannels == audio.Channels
            && video.AudioSampleRate == audio.SampleRate
            && (audio.Channels == 0 ? video.AudioCodec is null : video.AudioCodec == "aac" && video.AudioProfile == "LC");
    }

    private static (int Width, int Height) Fit(decimal width, decimal height, int denominator, int maxWidth, int maxHeight)
    {
        if (width <= (decimal)maxWidth * denominator && height <= (decimal)maxHeight * denominator)
        {
            return (EvenFloor(width, denominator), EvenFloor(height, denominator));
        }
        return width * maxHeight >= height * maxWidth
            ? (maxWidth, EvenFloor(height * maxWidth, width))
            : (EvenFloor(width * maxHeight, height), maxHeight);
    }

    private static int EvenFloor(decimal numerator, decimal denominator) =>
        (int)((numerator - numerator % (2 * denominator)) / denominator);

    private static (int Numerator, int Denominator) LimitFrameRate(int numerator, int denominator)
    {
        if (numerator >= 30L * denominator) return (30, 1);
        var divisor = GreatestCommonDivisor(numerator, denominator);
        return (numerator / divisor, denominator / divisor);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return left;
    }
}
