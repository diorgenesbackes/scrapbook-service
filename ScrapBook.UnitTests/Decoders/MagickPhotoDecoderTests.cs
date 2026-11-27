using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Decoders;

namespace ScrapBook.UnitTests.Decoders;

[Collection(MagickPhotoTestCollection.Name)]
public sealed class MagickPhotoDecoderTests
{
    private const string SyntheticPng = "89504e470d0a1a0a0000000d4948445200000020000000200802000000fc18eda3"
        + "0000002a49444154789c63489e9d4753c4306ac1a805a3168c5a306ac1a805a3168c5a306ac1a805a3160c150b00e564b04c740b4f38"
        + "0000000049454e44ae426082";

    [Fact]
    public void DecodesSyntheticPngPixels()
    {
        var result = MagickPhotoDecoder.Decode(Convert.FromHexString(SyntheticPng));

        Assert.Equal(new PhotoImageModel("image/png", "png", 32, 32, null), result);
    }

    [Theory]
    [InlineData("89504e470d0a1a0a")]
    [InlineData("ffd8ff")]
    [InlineData("0000001466747970686569630000000068656963")]
    [InlineData("474946383961")]
    public void RejectsTruncatedOrUnsupportedImageBytes(string bytes) =>
        Assert.Throws<PhotoContentInvalidException>(() => MagickPhotoDecoder.Decode(Convert.FromHexString(bytes)));
}
