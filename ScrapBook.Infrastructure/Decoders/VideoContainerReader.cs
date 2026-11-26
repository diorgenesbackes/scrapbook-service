using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Decoders;

internal static class VideoContainerReader
{
    private const int MaximumEbmlHeaderSize = 4096;
    private const int DocumentTypeId = 0x4282;

    public static async Task<bool> IsWebmAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[MaximumEbmlHeaderSize];
        var length = await file.ReadAtLeastAsync(header, header.Length, false, cancellationToken);
        ReadOnlySpan<byte> signature = [0x1a, 0x45, 0xdf, 0xa3];
        if (length < signature.Length || !header.AsSpan(0, signature.Length).SequenceEqual(signature)) return false;
        RequireWebm(header.AsSpan(signature.Length, length - signature.Length));
        return true;
    }

    private static void RequireWebm(ReadOnlySpan<byte> input)
    {
        var headerSize = ReadEbmlInteger(ref input, removeMarker: true);
        if (headerSize > (ulong)input.Length) throw new PhotoContentInvalidException();

        var header = input[..(int)headerSize];
        var hasType = false;
        while (!header.IsEmpty)
        {
            var id = ReadEbmlInteger(ref header, removeMarker: false);
            var size = ReadEbmlInteger(ref header, removeMarker: true);
            if (size > (ulong)header.Length) throw new PhotoContentInvalidException();
            if (id == DocumentTypeId)
            {
                if (hasType || !header[..(int)size].SequenceEqual("webm"u8)) throw new PhotoContentInvalidException();
                hasType = true;
            }
            header = header[(int)size..];
        }

        if (!hasType) throw new PhotoContentInvalidException();
    }

    private static ulong ReadEbmlInteger(ref ReadOnlySpan<byte> data, bool removeMarker)
    {
        if (data.IsEmpty || data[0] == 0) throw new PhotoContentInvalidException();
        var marker = 0x80;
        var width = 1;
        while ((data[0] & marker) == 0)
        {
            marker >>= 1;
            width++;
        }
        if (width > data.Length || !removeMarker && width > 4) throw new PhotoContentInvalidException();

        var value = (ulong)(removeMarker ? data[0] ^ marker : data[0]);
        foreach (var octet in data[1..width]) value = (value << 8) | octet;
        data = data[width..];
        return value;
    }
}
