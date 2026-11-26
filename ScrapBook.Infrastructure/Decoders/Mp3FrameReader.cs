using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Decoders;

internal static class Mp3FrameReader
{
    private static ReadOnlySpan<int> Mpeg1Bitrates => [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static ReadOnlySpan<int> Mpeg2Bitrates => [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static ReadOnlySpan<int> SampleRates => [44100, 48000, 32000];

    public static async Task RequireCompleteAsync(FileStream file, long offset, int size, Memory<byte> header,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || size < header.Length || offset > file.Length - size)
            throw new PhotoContentInvalidException();

        file.Position = offset;
        if (await file.ReadAtLeastAsync(header, header.Length, false, cancellationToken) != header.Length
            || !MatchesFrameSize(header.Span, size))
            throw new PhotoContentInvalidException();
    }

    private static bool MatchesFrameSize(ReadOnlySpan<byte> header, int size)
    {
        var version = (header[1] >> 3) & 3;
        var layer = (header[1] >> 1) & 3;
        var bitrateIndex = header[2] >> 4;
        var sampleRateIndex = (header[2] >> 2) & 3;
        if (header[0] != 255 || (header[1] & 0xe0) != 0xe0 || version == 1 || layer != 1
            || bitrateIndex == 0 || bitrateIndex == 15 || sampleRateIndex == 3) return false;

        var sampleRate = SampleRates[sampleRateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
        var bitrate = version == 3 ? Mpeg1Bitrates[bitrateIndex] : Mpeg2Bitrates[bitrateIndex];
        var padding = (header[2] >> 1) & 1;
        var frameSize = (version == 3 ? 144000 : 72000) * bitrate / sampleRate + padding;
        return size == frameSize;
    }
}
