using System.Buffers.Binary;
using ImageMagick;
using ImageMagick.Configuration;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

public static class MagickPhotoDecoder
{
    private const uint MaximumDimension = 12_000;
    private const ulong MaximumPixels = 64_000_000;
    private const ulong MemoryLimit = 384 * 1024 * 1024;
    private const string Policy = """
        <policymap>
          <policy domain="delegate" rights="none" pattern="*" />
          <policy domain="filter" rights="none" pattern="*" />
          <policy domain="path" rights="none" pattern="@*" />
          <policy domain="coder" rights="none" pattern="*" />
          <policy domain="coder" rights="read" pattern="{JPEG,PNG,HEIC}" />
          <policy domain="resource" name="map" value="0" />
        </policymap>
        """;

    public static PhotoImageModel Decode(byte[] bytes)
    {
        var (format, mediaType, container) = Detect(bytes);
        Configure();
        try
        {
            using var metadata = new MagickImage();
            metadata.Ping(bytes, new MagickReadSettings { Format = format });
            CheckDimensions(metadata.Width, metadata.Height);

            using var images = new MagickImageCollection();
            var hasWarning = false;
            images.Warning += (_, _) => hasWarning = true;
            images.Read(bytes, format);
            if (images.Count != 1 || hasWarning) throw new PhotoContentInvalidException();
            var image = images[0];
            CheckDimensions(image.Width, image.Height);
            var orientation = (short)image.Orientation;
            return new PhotoImageModel(mediaType, container, (int)image.Width, (int)image.Height,
                orientation is >= 1 and <= 8 ? orientation : null);
        }
        catch (MagickResourceLimitErrorException)
        {
            throw new PhotoDecoderBudgetException();
        }
        catch (MagickException)
        {
            throw new PhotoContentInvalidException();
        }
    }

    private static void Configure()
    {
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = Policy;
        MagickNET.Initialize(configuration);
        ResourceLimits.Thread = 1;
        ResourceLimits.Width = MaximumDimension;
        ResourceLimits.Height = MaximumDimension;
        ResourceLimits.Area = MaximumPixels;
        ResourceLimits.Memory = MemoryLimit;
        ResourceLimits.Disk = 0;
        ResourceLimits.MaxMemoryRequest = MemoryLimit;
        ResourceLimits.MaxProfileSize = 8 * 1024 * 1024;
        ResourceLimits.ListLength = 2;
        ResourceLimits.Time = 15;
    }

    private static void CheckDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0) throw new PhotoContentInvalidException();
        if (width > MaximumDimension || height > MaximumDimension || (ulong)width * height > MaximumPixels)
        {
            throw new PhotoDecoderBudgetException();
        }
    }

    private static (MagickFormat Format, string MediaType, string Container) Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            return (MagickFormat.Jpeg, "image/jpeg", "jpeg");
        }
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return (MagickFormat.Png, "image/png", "png");
        }
        if (bytes.Length < 16 || !bytes[4..8].SequenceEqual("ftyp"u8))
        {
            throw new PhotoContentInvalidException();
        }
        var boxLength = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (boxLength < 16 || boxLength > bytes.Length || boxLength % 4 != 0)
        {
            throw new PhotoContentInvalidException();
        }

        var heic = IsHeic(bytes[8..12]);
        var heif = bytes[8..12].SequenceEqual("mif1"u8);
        for (var offset = 16; offset < boxLength; offset += 4)
        {
            var brand = bytes.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
            {
                throw new PhotoContentInvalidException();
            }
            heic |= IsHeic(brand);
            heif |= brand.SequenceEqual("mif1"u8);
        }
        if (bytes[8..12].SequenceEqual("avif"u8) || bytes[8..12].SequenceEqual("avis"u8))
        {
            throw new PhotoContentInvalidException();
        }
        if (heic) return (MagickFormat.Heic, "image/heic", "heif");
        if (heif) return (MagickFormat.Heic, "image/heif", "heif");
        throw new PhotoContentInvalidException();
    }

    private static bool IsHeic(ReadOnlySpan<byte> brand) => brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
        || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8);
}
