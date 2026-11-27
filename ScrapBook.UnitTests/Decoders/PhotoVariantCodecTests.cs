using System.Buffers.Binary;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Decoders;

namespace ScrapBook.UnitTests.Decoders;

public sealed class PhotoVariantCodecTests
{
    private static readonly PhotoVariantSetModel Variants = new(1200, 2400,
        new([1, 2, 3], 1024, 2048), new([4, 5], 200, 400));

    [Fact]
    public async Task PreservesBothImagesAndOrientedSourceDimensionsAcrossTheProcessBoundaryAsync()
    {
        await using var buffer = new MemoryStream();
        await PhotoVariantCodec.WriteAsync(buffer, Variants, CancellationToken.None);
        buffer.Position = 0;

        var result = await PhotoVariantCodec.ReadAsync(buffer, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal((1200, 2400), (result.SourceWidth, result.SourceHeight));
        Assert.Equal((1024, 2048), (result.Image.Width, result.Image.Height));
        Assert.Equal(Variants.Image.Bytes, result.Image.Bytes);
        Assert.Equal((200, 400), (result.Thumbnail.Width, result.Thumbnail.Height));
        Assert.Equal(Variants.Thumbnail.Bytes, result.Thumbnail.Bytes);
    }

    [Fact]
    public async Task DistinguishesNoOutputFromAnInvalidHeaderAsync()
    {
        await using var empty = new MemoryStream();
        await using var truncated = new MemoryStream([1, 2]);

        Assert.Null(await PhotoVariantCodec.ReadAsync(empty, CancellationToken.None));
        await Assert.ThrowsAsync<PhotoDecoderUnavailableException>(() => PhotoVariantCodec.ReadAsync(truncated, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6 * 1024 * 1024 + 1)]
    public async Task RejectsUnsafeVariantLengthsBeforeAllocatingThePayloadAsync(int length)
    {
        var header = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), length);
        await using var buffer = new MemoryStream(header);

        await Assert.ThrowsAsync<PhotoDecoderUnavailableException>(() => PhotoVariantCodec.ReadAsync(buffer, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsUnconsumedDataAfterBothVariantsAsync()
    {
        await using var buffer = new MemoryStream();
        await PhotoVariantCodec.WriteAsync(buffer, Variants, CancellationToken.None);
        buffer.WriteByte(9);
        buffer.Position = 0;

        await Assert.ThrowsAsync<PhotoDecoderUnavailableException>(() => PhotoVariantCodec.ReadAsync(buffer, CancellationToken.None));
    }
}
