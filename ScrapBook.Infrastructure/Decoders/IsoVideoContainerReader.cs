using System.Buffers.Binary;
using System.Text;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Decoders;

internal sealed class IsoVideoContainerReader(FileStream file, bool requireLocal, bool inspectLocation)
{
    private const int MaximumBoxCount = 65_536;
    private const int MaximumDataReferencesPerTrack = 64;
    private const uint FileTypeBox = 0x66747970;
    private const uint MovieBox = 0x6d6f6f76;
    private const uint TrackBox = 0x7472616b;
    private const uint UserDataBox = 0x75647461;
    private const uint MetadataBox = 0x6d657461;
    private const uint KeysBox = 0x6b657973;
    private const uint MediaBox = 0x6d646961;
    private const uint MediaInformationBox = 0x6d696e66;
    private const uint DataInformationBox = 0x64696e66;
    private const uint FreeformTagBox = 0x2d2d2d2d;
    private const uint TagNameBox = 0x6e616d65;
    private const uint DataReferenceBox = 0x64726566;
    private readonly byte[] header = new byte[16];
    private int boxCount;
    private int trackCount;
    private bool movieFound;
    private bool hasLocation;
    private string? majorBrand;

    public static async Task<(string? MajorBrand, bool HasLocation, int TrackCount)> InspectAsync(string path, bool requireLocal,
        bool inspectLocation, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var reader = new IsoVideoContainerReader(file, requireLocal, inspectLocation);
        await reader.ReadBoxesAsync(0, file.Length, 0, 0, cancellationToken);
        if (!reader.movieFound || requireLocal && reader.trackCount == 0) throw new PhotoContentInvalidException();
        return (reader.majorBrand, reader.hasLocation, reader.trackCount);
    }

    private async Task<int> ReadBoxesAsync(long start, long end, uint parent, int depth, CancellationToken cancellationToken)
    {
        if (depth > 12) throw new PhotoContentInvalidException();

        var referenceBoxes = 0;
        while (start < end)
        {
            if (parent == UserDataBox && end - start == 4)
            {
                await ReadAsync(start, header.AsMemory(0, 4), cancellationToken);
                if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0) break;
            }

            var (type, payload, boxEnd) = await ReadHeaderAsync(start, end, cancellationToken);
            if (inspectLocation && IsLocationBox(type)) hasLocation = true;
            if (type == FileTypeBox && parent == 0)
            {
                if (majorBrand is not null || boxEnd - payload < 8) throw new PhotoContentInvalidException();
                await ReadAsync(payload, header.AsMemory(0, 4), cancellationToken);
                majorBrand = Encoding.Latin1.GetString(header, 0, 4);
            }
            if (type == MovieBox)
            {
                if (parent != 0 || movieFound) throw new PhotoContentInvalidException();
                movieFound = true;
                if (requireLocal) await ReadBoxesAsync(payload, boxEnd, type, depth + 1, cancellationToken);
            }
            else if (type == TrackBox && parent == MovieBox)
            {
                trackCount++;
                if (await ReadBoxesAsync(payload, boxEnd, type, depth + 1, cancellationToken) != 1)
                    throw new PhotoContentInvalidException();
            }
            else if (type == DataReferenceBox)
            {
                if (parent != DataInformationBox) throw new PhotoContentInvalidException();
                await RequireLocalReferencesAsync(payload, boxEnd, cancellationToken);
                referenceBoxes++;
            }
            else if (inspectLocation && type == KeysBox && parent == MetadataBox)
            {
                await InspectKeysAsync(payload, boxEnd, cancellationToken);
            }
            else if (inspectLocation && type == TagNameBox && parent == FreeformTagBox)
            {
                await InspectLocationTagAsync(payload + 4, boxEnd, cancellationToken);
            }
            else if (IsContainer(type, parent))
            {
                if (type == MetadataBox) payload = await MetadataStartAsync(payload, boxEnd, cancellationToken);
                referenceBoxes += await ReadBoxesAsync(payload, boxEnd, type, depth + 1, cancellationToken);
            }

            start = boxEnd;
        }

        return referenceBoxes;
    }

    private async Task<(uint Type, long Payload, long End)> ReadHeaderAsync(long start, long end,
        CancellationToken cancellationToken)
    {
        if (++boxCount > MaximumBoxCount || end - start < 8) throw new PhotoContentInvalidException();

        await ReadAsync(start, header.AsMemory(0, 8), cancellationToken);
        var shortSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        var headerSize = shortSize == 1 ? 16 : 8;
        if (end - start < headerSize) throw new PhotoContentInvalidException();

        ulong size = shortSize;
        if (shortSize == 1)
        {
            await ReadAsync(start + 8, header.AsMemory(8, 8), cancellationToken);
            size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
        }
        else if (shortSize == 0)
        {
            size = (ulong)(end - start);
        }

        if (size < (ulong)headerSize || size > (ulong)(end - start)) throw new PhotoContentInvalidException();
        return (type, start + headerSize, start + (long)size);
    }

    private async Task RequireLocalReferencesAsync(long start, long end, CancellationToken cancellationToken)
    {
        if (end - start < 8) throw new PhotoContentInvalidException();

        await ReadAsync(start, header.AsMemory(0, 8), cancellationToken);
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0 || count is 0 or > MaximumDataReferencesPerTrack)
            throw new PhotoContentInvalidException();

        start += 8;
        for (var i = 0; i < count; i++)
        {
            var (type, payload, boxEnd) = await ReadHeaderAsync(start, end, cancellationToken);
            if (type != 0x75726c20 || boxEnd - payload != 4) throw new PhotoContentInvalidException();

            await ReadAsync(payload, header.AsMemory(0, 4), cancellationToken);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) != 1) throw new PhotoContentInvalidException();
            start = boxEnd;
        }

        if (start != end) throw new PhotoContentInvalidException();
    }

    private async Task<long> MetadataStartAsync(long start, long end, CancellationToken cancellationToken)
    {
        if (end - start < 4) throw new PhotoContentInvalidException();
        await ReadAsync(start, header.AsMemory(0, 4), cancellationToken);
        return BinaryPrimitives.ReadUInt32BigEndian(header) == 0 ? start + 4 : start;
    }

    private async Task InspectKeysAsync(long start, long end, CancellationToken cancellationToken)
    {
        if (end - start < 8) throw new PhotoContentInvalidException();
        await ReadAsync(start, header.AsMemory(0, 8), cancellationToken);
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (count > 4096) throw new PhotoContentInvalidException();

        start += 8;
        for (var i = 0; i < count; i++)
        {
            if (end - start < 8) throw new PhotoContentInvalidException();
            await ReadAsync(start, header.AsMemory(0, 8), cancellationToken);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size <= 8 || size > end - start || size > 4096) throw new PhotoContentInvalidException();

            await InspectLocationTagAsync(start + 8, start + size, cancellationToken);
            start += size;
        }

        if (start != end) throw new PhotoContentInvalidException();
    }

    private async Task InspectLocationTagAsync(long start, long end, CancellationToken cancellationToken)
    {
        var size = end - start;
        if (size is <= 0 or > 4096) throw new PhotoContentInvalidException();
        var key = new byte[size];
        await ReadAsync(start, key, cancellationToken);
        if (MediaLocationMetadata.IsLocationTag(Encoding.UTF8.GetString(key))) hasLocation = true;
    }

    private async Task ReadAsync(long start, Memory<byte> data, CancellationToken cancellationToken)
    {
        file.Position = start;
        await file.ReadExactlyAsync(data, cancellationToken);
    }

    private static bool IsContainer(uint type, uint parent) => (type, parent) switch
    {
        (MediaBox, TrackBox) or (MediaInformationBox, MediaBox) or (DataInformationBox, MediaInformationBox) => true,
        (UserDataBox or MetadataBox or 0x696c7374 or FreeformTagBox, _) => true,
        _ => false
    };

    private static bool IsLocationBox(uint type) => type is 0x6c6f6369 or 0xa978797a or 0xa9677073 or 0x47505320 or 0x67707320
        or 0x47504d46 or 0x67706d66 or 0x584d505f or 0x786d6c20 or 0x75756964 or 0x45786966 or 0x65786966;
}
