using System.Globalization;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Decoders;

public sealed class AudioTranscodingDecoder(string executablePath)
{
    private const long MaximumInputBytes = 64_000_000;
    private const decimal RemuxDurationTolerance = 0.100m;
    private static readonly TimeSpan NormalizeTimeout = TimeSpan.FromSeconds(30);
    private readonly FfprobeAudioDecoder probe = new(executablePath);

    internal async Task<AudioTranscodingInputModel> PrepareAsync(string path, CancellationToken cancellationToken)
    {
        var source = await probe.ProbeAsync(path, false, cancellationToken);
        var track = source.Primary;
        if (track.Channels < 1) throw new PhotoContentInvalidException();
        var duration = track.Duration(source.Container == "webm");
        var remux = RemuxFormat(source.Container, track.Codec);
        var standardChannels = track.Channels <= 2;
        if (standardChannels && source.StreamCount == 1 && remux == source.Container && source.Container != "m4a")
            return new AudioTranscodingInputModel(path, source.MediaType, duration, track.Channels);

        var normalizedPath = Path.Combine(Path.GetDirectoryName(path)!, "normalized");
        if (standardChannels && remux is not null && await TryRemuxAsync(path, normalizedPath, source, remux, duration,
            cancellationToken) is { } normalized)
            return new AudioTranscodingInputModel(normalizedPath, normalized.MediaType, duration, track.Channels);

        return await NormalizePcmAsync(path, normalizedPath, source, duration, cancellationToken);
    }

    internal Task<AudioDecodingModel> InspectPlaybackAsync(string path, CancellationToken cancellationToken) =>
        probe.ProbeAsync(path, true, cancellationToken);

    private async Task<AudioDecodingModel?> TryRemuxAsync(string inputPath, string outputPath, AudioDecodingModel source,
        string format, decimal duration, CancellationToken cancellationToken)
    {
        try
        {
            var muxer = format == "m4a" ? "mp4" : format;
            string[] options = format == "m4a" ? ["-movflags", "+faststart"] : [];
            await RewriteAsync(inputPath, outputPath, source, ["-c:a", "copy", .. options, "-f", muxer], cancellationToken);
            var output = await ReadNormalizedAsync(outputPath, cancellationToken);
            return output.Container == format && output.Primary.Codec == source.Primary.Codec
                && output.Primary.Channels == source.Primary.Channels && MatchesDuration(output, duration) ? output : null;
        }
        catch (PhotoContentInvalidException)
        {
            return null;
        }
    }

    private async Task<AudioTranscodingInputModel> NormalizePcmAsync(string inputPath, string outputPath,
        AudioDecodingModel source, decimal duration, CancellationToken cancellationToken)
    {
        var channels = Math.Min(source.Primary.Channels, 2);
        string[] options = ["-c:a", "pcm_s16le", "-ar", "48000", "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "-f", "wav"];
        await RewriteAsync(inputPath, outputPath, source, options, cancellationToken);
        var output = await ReadNormalizedAsync(outputPath, cancellationToken);
        if (output.Container != "wav" || output.Primary.Codec != "pcm_s16le" || output.Primary.Channels != channels
            || output.Primary.SampleRate != 48_000 || !MatchesDuration(output, duration))
            throw new PhotoContentInvalidException();

        return new AudioTranscodingInputModel(outputPath, output.MediaType, duration, channels);
    }

    private async Task RewriteAsync(string inputPath, string outputPath, AudioDecodingModel source, string[] options,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(NormalizeTimeout);
        var ffmpeg = Path.Combine(Path.GetDirectoryName(executablePath)!, "ffmpeg");
        var input = MediaDecoderProcess.InputOptions(FfprobeAudioDecoder.FormatWhitelist(source.Container), source.Container == "m4a");
        var stream = "0:" + source.PrimaryIndex.ToString(CultureInfo.InvariantCulture);
        string[] arguments = ["-hide_banner", "-nostdin", "-nostats", "-y", "-xerror", "-max_error_rate", "0",
            "-filter_threads", "1", .. input, "-i", inputPath, "-map", stream, "-map_metadata", "-1", "-map_metadata:s:a", "-1",
            "-map_chapters", "-1", "-threads", "1", "-fflags", "+bitexact", "-flags:a", "+bitexact",
            "-fs", MaximumInputBytes.ToString(CultureInfo.InvariantCulture), .. options, outputPath];
        try
        {
            await MediaDecoderProcess.RunAsync(ffmpeg, inputPath, arguments,
                (reader, token) => MediaDecoderProcess.ReadBoundedAsync(reader, 1024, token), budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhotoDecoderBudgetException();
        }
    }

    private async Task<AudioDecodingModel> ReadNormalizedAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length is <= 0 or >= MaximumInputBytes) throw new PhotoContentInvalidException();
        var audio = await probe.ProbeAsync(path, false, cancellationToken);
        return audio.StreamCount == 1 ? audio : throw new PhotoContentInvalidException();
    }

    private static bool MatchesDuration(AudioDecodingModel audio, decimal duration) =>
        Math.Abs(audio.Primary.Duration(false) - duration) <= RemuxDurationTolerance;

    private static string? RemuxFormat(string container, string codec) => (container, codec) switch
    {
        ("mp3", "mp3") => "mp3",
        ("m4a", "aac" or "flac") => "m4a",
        ("wav", "pcm_s16le" or "pcm_s24le" or "pcm_u8") => "wav",
        ("ogg" or "webm", "flac" or "opus" or "vorbis") => "ogg",
        _ => null
    };
}
