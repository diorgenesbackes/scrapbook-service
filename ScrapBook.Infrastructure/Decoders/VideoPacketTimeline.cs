using System.Globalization;
using System.Numerics;
using ScrapBook.Domain.Exceptions;
using static ScrapBook.Infrastructure.Decoders.FfprobeVideoDecoder;

namespace ScrapBook.Infrastructure.Decoders;

internal sealed class VideoPacketTimeline(int videoIndex, MediaRationalModel videoTimeBase, int? audioIndex,
    MediaRationalModel? audioTimeBase, int audioSampleRate)
{
    private const int MaximumPacketLines = 500_000;
    private readonly List<(long Position, long Length)> frames = [];
    private decimal? audioFirst;
    private decimal? audioLast;

    public async Task<VideoPacketTimeline> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var count = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (++count > MaximumPacketLines || line.Length > 1024) throw new PhotoDecoderBudgetException();
            if (!string.IsNullOrWhiteSpace(line)) AddPacket(line);
        }

        if (frames.Count == 0 || audioIndex is not null && (audioFirst is null || audioLast <= audioFirst))
            throw new PhotoContentInvalidException();

        frames.Sort((left, right) => left.Position.CompareTo(right.Position));
        return this;
    }

    public (MediaRationalModel Rate, bool Variable, decimal Duration) Measure(MediaRationalModel? nominal,
        MediaRationalModel? average, bool webm, bool source)
    {
        var first = frames[0].Position;
        var (last, lastLength) = frames[^1];
        if (frames.Count == 1)
        {
            if (lastLength <= 0) throw new PhotoContentInvalidException();
            return (Rate(1, lastLength), false, PresentationDuration(first, last, lastLength, webm));
        }

        var shortest = long.MaxValue;
        long longest = 0;
        for (var index = 1; index < frames.Count; index++)
        {
            var gap = checked(frames[index].Position - frames[index - 1].Position);
            if (gap <= 0) throw new PhotoContentInvalidException();
            shortest = Math.Min(shortest, gap);
            longest = Math.Max(longest, gap);
        }

        var duration = PresentationDuration(first, last, lastLength > 0 ? lastLength : shortest, webm);
        if (!webm && shortest == longest) return (Rate(1, shortest), false, duration);
        if (longest - shortest <= 1)
        {
            if (FollowsRate(nominal)) return (nominal!.Value, false, duration);
            if (FollowsRate(average)) return (average!.Value, false, duration);
            if (shortest == longest) return (Rate(1, shortest), false, duration);
        }

        var rate = source ? Rate(1, longest) : Rate(frames.Count - 1, checked(last - first));
        return (rate, true, duration);
    }

    private bool FollowsRate(MediaRationalModel? rate)
    {
        if (rate is not { } cadence) return false;

        var ticksPerFrame = (decimal)cadence.Denominator * videoTimeBase.Denominator
            / ((decimal)cadence.Numerator * videoTimeBase.Numerator);
        var start = frames[0].Position;
        for (var index = 1; index < frames.Count; index++)
        {
            if (Math.Abs((decimal)frames[index].Position - start - index * ticksPerFrame) > 1) return false;
            var gap = (decimal)frames[index].Position - frames[index - 1].Position;
            if (Math.Abs(gap - ticksPerFrame) >= 1) return false;
        }
        return true;
    }

    private MediaRationalModel Rate(int frameCount, long ticks) => MediaRationalModel.Reduce(
        (BigInteger)frameCount * videoTimeBase.Denominator, (BigInteger)ticks * videoTimeBase.Numerator);

    private decimal PresentationDuration(long first, long last, long lastLength, bool webm)
    {
        var start = Seconds(first, videoTimeBase);
        var end = Seconds(last, videoTimeBase) + Seconds(lastLength, videoTimeBase);
        if (audioFirst is not { } audioStart || audioLast is not { } audioEnd) return end - start;
        if (webm)
        {
            var tick = Seconds(1, videoTimeBase);
            audioStart = Math.Max(0, audioStart);
            audioEnd = decimal.Floor(audioEnd / tick) * tick;
        }

        return Math.Max(end, audioEnd) - Math.Min(start, audioStart);
    }

    private void AddPacket(ReadOnlySpan<char> packet)
    {
        if (!packet.StartsWith("packet|", StringComparison.Ordinal)
            || !int.TryParse(PacketField(packet, "stream_index="), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            throw new PhotoContentInvalidException();
        if (index != videoIndex && index != audioIndex) return;

        var flags = PacketField(packet, "flags=");
        if (flags.Contains('C')) throw new PhotoContentInvalidException();
        if (flags.Contains('D')) return;
        if (!long.TryParse(PacketField(packet, "pts="), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pts))
            throw new PhotoContentInvalidException();

        var duration = long.TryParse(PacketField(packet, "duration="), NumberStyles.None, CultureInfo.InvariantCulture,
            out var length) ? length : 0;
        if (index == videoIndex)
            frames.Add((pts, duration));
        else
            AddAudioPacket(packet, pts, duration, audioTimeBase!.Value);
    }

    private void AddAudioPacket(ReadOnlySpan<char> packet, long pts, long duration, MediaRationalModel timeBase)
    {
        var begin = Seconds(pts, timeBase) + AudioTimeOffset(packet, "side_datum/skip_samples:skip_samples=");
        var end = Seconds(pts, timeBase) + Seconds(duration, timeBase)
            - AudioTimeOffset(packet, "side_datum/skip_samples:discard_padding=");
        if (end < begin) return;

        audioFirst = Math.Min(audioFirst ?? begin, begin);
        audioLast = Math.Max(audioLast ?? end, end);
    }

    private decimal AudioTimeOffset(ReadOnlySpan<char> packet, string name) =>
        int.TryParse(PacketField(packet, name), NumberStyles.None, CultureInfo.InvariantCulture, out var samples)
            ? (decimal)samples / audioSampleRate : 0;

    private static decimal Seconds(long ticks, MediaRationalModel timeBase) =>
        (decimal)ticks * timeBase.Numerator / timeBase.Denominator;
}
