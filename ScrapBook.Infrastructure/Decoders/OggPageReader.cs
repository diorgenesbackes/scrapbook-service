using System.Buffers.Binary;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Decoders;

internal static class OggPageReader
{
    public static async Task RequireCompleteAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var activeStreams = new HashSet<uint>();
        var header = new byte[27];
        var segments = new byte[255];
        while (file.Position < file.Length)
        {
            if (await file.ReadAtLeastAsync(header, header.Length, false, cancellationToken) != header.Length
                || !header.AsSpan(0, 4).SequenceEqual("OggS"u8) || header[4] != 0)
                throw new PhotoContentInvalidException();

            var serial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
            var flags = header[5];
            var segmentCount = header[26];
            if (await file.ReadAtLeastAsync(segments.AsMemory(0, segmentCount), segmentCount, false, cancellationToken)
                != segmentCount)
                throw new PhotoContentInvalidException();

            var payloadSize = 0;
            foreach (var length in segments.AsSpan(0, segmentCount)) payloadSize += length;
            if (payloadSize > file.Length - file.Position) throw new PhotoContentInvalidException();
            file.Position += payloadSize;
            UpdateStreams(activeStreams, serial, flags);
        }

        if (activeStreams.Count != 0) throw new PhotoContentInvalidException();
    }

    private static void UpdateStreams(HashSet<uint> activeStreams, uint serial, byte flags)
    {
        if ((flags & 2) != 0)
        {
            if (activeStreams.Count == 16 || !activeStreams.Add(serial)) throw new PhotoContentInvalidException();
        }
        else if (!activeStreams.Contains(serial))
        {
            throw new PhotoContentInvalidException();
        }

        if ((flags & 4) != 0) activeStreams.Remove(serial);
    }
}
