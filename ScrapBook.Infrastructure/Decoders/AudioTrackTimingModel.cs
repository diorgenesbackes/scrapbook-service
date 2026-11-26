using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Decoders;

internal sealed class AudioTrackTimingModel(string codec, bool isDefault, int sampleRate,
    int timeBaseNumerator, int timeBaseDenominator)
{
    private readonly decimal timestampStep = (decimal)timeBaseNumerator / timeBaseDenominator;
    private long decodedSamples;
    private long? firstTimestamp;
    private decimal? lastEnd;

    public string Codec => codec;
    public bool IsDefault => isDefault;
    public decimal? PresentationStart => firstTimestamp is { } start ? Timestamp(start) : null;

    public void AddFrame(int samples, long? timestamp)
    {
        decodedSamples += samples;
        if (timestamp is not { } position) return;
        firstTimestamp = Math.Min(firstTimestamp ?? position, position);
        var end = Timestamp(position) + (decimal)samples / sampleRate;
        lastEnd = Math.Max(lastEnd ?? end, end);
    }

    public decimal Duration(bool webm)
    {
        if (decodedSamples <= 0) throw new PhotoContentInvalidException();
        var sampleDuration = (decimal)decodedSamples / sampleRate;
        if (firstTimestamp is not { } start || lastEnd is not { } end) return sampleDuration;
        var interval = end - Timestamp(start);
        return webm && interval - sampleDuration <= timestampStep
            ? sampleDuration
            : Math.Max(sampleDuration, interval);
    }

    private decimal Timestamp(long timestamp) => (decimal)timestamp * timeBaseNumerator / timeBaseDenominator;
}
