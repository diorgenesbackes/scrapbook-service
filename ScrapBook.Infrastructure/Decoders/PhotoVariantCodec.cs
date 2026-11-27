using System.Buffers.Binary;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

public static class PhotoVariantCodec
{
    private const int MaximumBytes = 6 * 1024 * 1024;

    public static async Task<PhotoVariantSetModel?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var dimensions = new byte[8];
        var count = await stream.ReadAtLeastAsync(dimensions, dimensions.Length, throwOnEndOfStream: false, cancellationToken);
        if (count == 0) return null;
        if (count != dimensions.Length) throw new PhotoDecoderUnavailableException();
        var image = await ReadVariantAsync(stream, cancellationToken);
        var thumbnail = await ReadVariantAsync(stream, cancellationToken);
        if (await stream.ReadAsync(dimensions.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw new PhotoDecoderUnavailableException();
        }
        return new PhotoVariantSetModel(BinaryPrimitives.ReadInt32LittleEndian(dimensions),
            BinaryPrimitives.ReadInt32LittleEndian(dimensions.AsSpan(4)), image, thumbnail);
    }

    public static async Task WriteAsync(Stream stream, PhotoVariantSetModel variants, CancellationToken cancellationToken)
    {
        var dimensions = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, variants.SourceWidth);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions.AsSpan(4), variants.SourceHeight);
        await stream.WriteAsync(dimensions, cancellationToken);
        await WriteVariantAsync(stream, variants.Image, cancellationToken);
        await WriteVariantAsync(stream, variants.Thumbnail, cancellationToken);
    }

    private static async Task<PhotoVariantModel> ReadVariantAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (length is <= 0 or > MaximumBytes) throw new PhotoDecoderUnavailableException();
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return new PhotoVariantModel(bytes, BinaryPrimitives.ReadInt32LittleEndian(header),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)));
    }

    private static async Task WriteVariantAsync(Stream stream, PhotoVariantModel variant, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header, variant.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), variant.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), variant.Bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(variant.Bytes, cancellationToken);
    }
}
