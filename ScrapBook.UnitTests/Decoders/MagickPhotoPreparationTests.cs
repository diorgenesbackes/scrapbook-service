using ImageMagick;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Decoders;

namespace ScrapBook.UnitTests.Decoders;

[Collection(MagickPhotoTestCollection.Name)]
public sealed class MagickPhotoPreparationTests
{
    private static readonly PhotoPreparationProfileModel Profile = new(2048, 400, 85);

    [Fact]
    public async Task RotatesThePixelsAndRemovesGpsFromBothSizedJpegsAsync()
    {
        var input = await ReadFixtureAsync("portrait-gps.jpg");
        using var original = new MagickImage(input);
        Assert.Equal(Orientation.RightTop, original.Orientation);
        var exif = original.GetExifProfile();
        Assert.NotNull(exif);
        Assert.Contains(exif.Values, value => value.Tag == ExifTag.GPSLatitude);
        Assert.Contains(exif.Values, value => value.Tag == ExifTag.GPSLongitude);

        var variants = MagickPhotoDecoder.Prepare(input, Profile);

        Assert.Equal((1200, 2400), (variants.SourceWidth, variants.SourceHeight));
        AssertJpeg(variants.Image, 1024, 2048, AssertRedAboveBlue);
        AssertJpeg(variants.Thumbnail, 200, 400, AssertRedAboveBlue);
    }

    [Fact]
    public async Task FlattensTransparentPngOnWhiteWithoutUpscalingAsync()
    {
        var input = await ReadFixtureAsync("transparency.png");

        var variants = MagickPhotoDecoder.Prepare(input, Profile);

        Assert.Equal((96, 60), (variants.SourceWidth, variants.SourceHeight));
        AssertJpeg(variants.Image, 96, 60, AssertWhiteAndBlue);
        AssertJpeg(variants.Thumbnail, 96, 60, AssertWhiteAndBlue);
    }

    [Fact]
    public async Task ConvertsARealHeicIntoBothRequiredJpegsAsync()
    {
        var input = await ReadFixtureAsync("palette.heic");

        var variants = MagickPhotoDecoder.Prepare(input, Profile);

        Assert.Equal((32, 32), (variants.SourceWidth, variants.SourceHeight));
        AssertJpeg(variants.Image, 32, 32);
        AssertJpeg(variants.Thumbnail, 32, 32);
    }

    [Theory]
    [InlineData("ffd8ff")]
    [InlineData("89504e470d0a1a0a")]
    [InlineData("474946383961")]
    public void RejectsTruncatedOrUnsupportedSources(string hex)
    {
        Assert.Throws<PhotoContentInvalidException>(() => MagickPhotoDecoder.Prepare(Convert.FromHexString(hex), Profile));
    }

    private static Task<byte[]> ReadFixtureAsync(string name) =>
        File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Decoders", "Fixtures", name), CancellationToken.None);

    private static void AssertJpeg(PhotoVariantModel variant, int width, int height, Action<MagickImage>? assertPixels = null)
    {
        Assert.Equal((width, height), (variant.Width, variant.Height));
        using var image = new MagickImage(variant.Bytes);
        Assert.Equal(MagickFormat.Jpeg, image.Format);
        Assert.Equal(((uint)width, (uint)height), (image.Width, image.Height));
        Assert.False(image.HasAlpha);
        Assert.Null(image.GetExifProfile());
        Assert.DoesNotContain(image.ProfileNames, name => name.Equals("iptc", StringComparison.OrdinalIgnoreCase)
            || name.Equals("xmp", StringComparison.OrdinalIgnoreCase));
        assertPixels?.Invoke(image);
    }

    private static void AssertRedAboveBlue(MagickImage image)
    {
        using var pixels = image.GetPixels();
        var top = pixels.GetPixel((int)image.Width / 2, (int)image.Height / 4);
        var bottom = pixels.GetPixel((int)image.Width / 2, (int)image.Height * 3 / 4);
        Assert.True(top.GetChannel(0) > top.GetChannel(2) + 50);
        Assert.True(bottom.GetChannel(2) > bottom.GetChannel(0) + 50);
    }

    private static void AssertWhiteAndBlue(MagickImage image)
    {
        using var pixels = image.GetPixels();
        var white = pixels.GetPixel((int)image.Width / 4, (int)image.Height / 2);
        var blue = pixels.GetPixel((int)image.Width * 3 / 4, (int)image.Height / 2);
        Assert.True(white.GetChannel(0) > 240 && white.GetChannel(1) > 240 && white.GetChannel(2) > 240);
        Assert.True(blue.GetChannel(2) > blue.GetChannel(0) + 50);
    }
}
