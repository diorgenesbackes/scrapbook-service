using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

internal static class MediaVideoProfileService
{
    private const int EncoderPixelStep = 2;

    internal static MediaVideoProfileModel? Create(MediaVideoPropertiesModel? source)
    {
        if (source is not
            {
                Container: "mp4" or "mov" or "webm", VideoCodec.Length: > 0,
                Width: > 0, Height: > 0, PixelAspectNumerator: > 0, PixelAspectDenominator: > 0,
                Duration: > 0 and <= 300, FrameRateNumerator: > 0, FrameRateDenominator: > 0,
                Rotation: 0 or 90 or -90 or 180 or -180 or 270 or -270, AudioChannels: >= 0
            }
            || (source.AudioChannels == 0 ? source.AudioCodec is not null : string.IsNullOrWhiteSpace(source.AudioCodec)))
        {
            return null;
        }

        var rotation = (source.Rotation + 360) % 360;
        var (width, height) = DisplaySize(source, rotation);
        var (rasterWidth, rasterHeight) = rotation is 90 or 270 ? (source.Height, source.Width) : (source.Width, source.Height);
        var maxWidth = Math.Min(rasterWidth, width > height ? 1920 : 1080);
        var maxHeight = Math.Min(rasterHeight, width < height ? 1920 : 1080);
        var (fittedWidth, fittedHeight) = Fit(width, height, source.PixelAspectDenominator, maxWidth, maxHeight);
        if (fittedWidth == 0 || fittedHeight == 0) return null;

        var (numerator, denominator) = LimitFrameRate(source.FrameRateNumerator, source.FrameRateDenominator);
        return new(fittedWidth, fittedHeight, numerator, denominator, rotation);
    }

    internal static bool MatchesOutput(MediaAudioVariantModel variant, MediaVideoPropertiesModel source,
        MediaVideoProfileModel profile, MediaAudioProfileModel audio)
    {
        if (variant.Video is not
            {
                Container: "mp4", VideoCodec: "h264", Rotation: 0, VariableFrameRate: false, HasLocationMetadata: false,
                PixelAspectNumerator: > 0, FrameRateNumerator: > 0, FrameRateDenominator: > 0, Duration: > 0
            } video || variant.MediaType != "video/mp4" || variant.Container != video.Container
            || variant.Duration != video.Duration || !MatchesGeometry(video, source, profile)
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

    private static bool MatchesGeometry(MediaVideoPropertiesModel video, MediaVideoPropertiesModel source, MediaVideoProfileModel profile)
    {
        var (width, height) = DisplaySize(source, profile.Rotation);
        return WithinDimension(video.Width, profile.Width) && WithinDimension(video.Height, profile.Height)
            && Math.Abs(video.Width * height - video.Height * width) <= EncoderPixelStep * Math.Max(width, height);
    }

    private static bool WithinDimension(int actual, int expected) =>
        actual > 0 && actual % EncoderPixelStep == 0 && actual <= expected && actual >= expected - EncoderPixelStep;

    private static (decimal Width, decimal Height) DisplaySize(MediaVideoPropertiesModel source, int rotation)
    {
        var width = (decimal)source.Width * source.PixelAspectNumerator;
        var height = (decimal)source.Height * source.PixelAspectDenominator;
        return rotation is 90 or 270 ? (height, width) : (width, height);
    }

    private static (int Width, int Height) Fit(decimal width, decimal height, int denominator, int maxWidth, int maxHeight)
    {
        if (width <= (decimal)maxWidth * denominator && height <= (decimal)maxHeight * denominator)
        {
            return (EvenFloor(width, denominator), EvenFloor(height, denominator));
        }
        return width * maxHeight >= height * maxWidth
            ? (EvenFloor(maxWidth, 1), EvenFloor(height * maxWidth, width))
            : (EvenFloor(width * maxHeight, height), EvenFloor(maxHeight, 1));
    }

    private static int EvenFloor(decimal numerator, decimal denominator) =>
        (int)((numerator - numerator % (EncoderPixelStep * denominator)) / denominator);

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
