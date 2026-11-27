using System.Globalization;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

public sealed class VideoTranscodingDecoder(string executablePath)
{
    internal const long MaximumInputBytes = 512_000_000;
    private const decimal RemuxDurationTolerance = 0.150m;
    private static readonly TimeSpan StageBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PlaybackBudget = TimeSpan.FromSeconds(30);
    private readonly FfprobeVideoProfileDecoder probe = new(executablePath);

    internal async Task<VideoTranscodingInputModel> PrepareAsync(string path, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StageBudget);
        try
        {
            var original = await probe.DecodeAsync(path, true, false, budget.Token);
            var video = original.Properties;
            var normalizeVideo = video.Rotation != 0 && !SupportsRotation(original);
            var format = normalizeVideo ? (video.Container == "mov" ? "mov" : "mp4") : SelectStagingFormat(video);
            if (format == "webm" && video.Container == "webm" && SupportsAudioCopy("webm", video.AudioCodec)
                && original.StreamCount == ExpectedStreamCount(original) && !video.HasLocationMetadata)
                return new VideoTranscodingInputModel(path, original.MediaType, video);

            var outputPath = Path.Combine(Path.GetDirectoryName(path)!, "normalized");
            return await RewriteAsync(path, outputPath, original, format, normalizeVideo, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhotoDecoderBudgetException();
        }
    }

    internal async Task<MediaVideoPropertiesModel> InspectPlaybackAsync(string path, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PlaybackBudget);
        try
        {
            return (await probe.DecodeAsync(path, false, true, budget.Token)).Properties;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhotoDecoderBudgetException();
        }
    }

    private async Task<VideoTranscodingInputModel> RewriteAsync(string inputPath, string outputPath, VideoDecodingModel original,
        string format, bool normalizeVideo, CancellationToken cancellationToken)
    {
        var video = original.Properties;
        var videoOptions = normalizeVideo ? NormalizedVideoOptions(original) : ["-c:v", "copy"];
        if (SupportsAudioCopy(format, video.AudioCodec))
        {
            try
            {
                return await RewriteAndInspectAsync(inputPath, outputPath, original, format, videoOptions, ["-c:a", "copy"],
                    normalizeVideo, cancellationToken);
            }
            catch (PhotoContentInvalidException) when (original.AudioIndex is not null)
            {
            }
        }

        var channels = Math.Min(video.AudioChannels, 2).ToString(CultureInfo.InvariantCulture);
        var codec = NormalizedAudioOptions(format, new FileInfo(inputPath).Length);
        string[] audio = [.. codec, "-ar", "48000", "-ac", channels];
        return await RewriteAndInspectAsync(inputPath, outputPath, original, format, videoOptions, audio, normalizeVideo,
            cancellationToken);
    }

    private async Task<VideoTranscodingInputModel> RewriteAndInspectAsync(string inputPath, string outputPath,
        VideoDecodingModel original, string format, string[] videoOptions, string[] audio, bool normalizeVideo,
        CancellationToken cancellationToken)
    {
        var ffmpeg = Path.Combine(Path.GetDirectoryName(executablePath)!, "ffmpeg");
        var input = FfprobeVideoProfileDecoder.InputOptions(original.Properties.Container == "webm");
        var videoStream = "0:" + original.VideoIndex.ToString(CultureInfo.InvariantCulture);
        string[] audioStream = original.AudioIndex is { } index ? ["-map", "0:" + index.ToString(CultureInfo.InvariantCulture)] : [];
        string[] movOptions = format == "webm" ? [] : ["-movflags", "+faststart", "-write_tmcd", "0"];
        string[] arguments = ["-hide_banner", "-nostdin", "-nostats", "-y", "-xerror", "-max_error_rate", "0",
            "-filter_threads", "1", .. input, "-noautorotate", "-i", inputPath, "-map", videoStream, .. audioStream,
            "-map_metadata", "-1", "-map_metadata:s:v", "-1", "-map_metadata:s:a", "-1", "-map_chapters", "-1",
            "-threads", "1", "-fflags", "+bitexact", .. videoOptions, .. audio, .. movOptions,
            "-fs", MaximumInputBytes.ToString(CultureInfo.InvariantCulture), "-f", format, outputPath];
        await MediaDecoderProcess.RunAsync(ffmpeg, inputPath, arguments,
            (reader, token) => MediaDecoderProcess.ReadBoundedAsync(reader, 1024, token), MaximumInputBytes, cancellationToken);

        if (new FileInfo(outputPath).Length is <= 0 or > MaximumInputBytes) throw new PhotoContentInvalidException();
        var staged = await probe.DecodeAsync(outputPath, false, false, cancellationToken);
        RequireEquivalentInput(staged, original, format, normalizeVideo);

        var source = original.Properties;
        var properties = staged.Properties with
        {
            Duration = source.Duration,
            FrameRateNumerator = source.FrameRateNumerator,
            FrameRateDenominator = source.FrameRateDenominator,
            VariableFrameRate = source.VariableFrameRate
        };
        return new VideoTranscodingInputModel(outputPath, staged.MediaType, properties);
    }

    private static void RequireEquivalentInput(VideoDecodingModel staged, VideoDecodingModel original, string format,
        bool normalizeVideo)
    {
        var source = original.Properties;
        var output = staged.Properties;
        if (staged.StreamCount != ExpectedStreamCount(original) || staged.VideoIndex != 0
            || staged.VideoPacketCount != original.VideoPacketCount
            || (original.AudioIndex is null ? staged.AudioIndex is not null : staged.AudioIndex != 1)
            || output.Container != format || output.VideoCodec != (normalizeVideo ? "h264" : source.VideoCodec)
            || output.HasLocationMetadata || (output.Rotation != 0 && !SupportsRotation(staged))
            || (normalizeVideo && staged.PixelFormat != "yuv420p")
            || !SupportsAudioCopy(format, output.AudioCodec) || output.Rotation != source.Rotation
            || output.Width != source.Width || output.Height != source.Height
            || output.PixelAspectNumerator != source.PixelAspectNumerator
            || output.PixelAspectDenominator != source.PixelAspectDenominator
            || Math.Abs(output.Duration - source.Duration) > RemuxDurationTolerance)
            throw new PhotoContentInvalidException();
    }

    private static int ExpectedStreamCount(VideoDecodingModel video) => video.AudioIndex is null ? 1 : 2;

    private static string SelectStagingFormat(MediaVideoPropertiesModel video)
    {
        if (SupportsVideoCopy(video.Container, video.VideoCodec)) return video.Container;
        if (SupportsVideoCopy("mp4", video.VideoCodec)) return "mp4";
        if (SupportsVideoCopy("mov", video.VideoCodec)) return "mov";
        if (video.Rotation == 0 && SupportsVideoCopy("webm", video.VideoCodec)) return "webm";
        throw new PhotoContentInvalidException();
    }

    private static bool SupportsVideoCopy(string container, string codec) => (container, codec) switch
    {
        ("webm", "vp8" or "vp9") => true,
        ("mp4", "av1" or "vp9" or "vc1") => true,
        ("mov", "prores" or "dvvideo" or "mjpeg" or "qtrle") => true,
        ("mp4" or "mov", "h264" or "hevc" or "h261" or "h262" or "h263" or "mpeg2video" or "mpeg4" or "jpeg2000"
            or "rawvideo") => true,
        _ => false
    };

    private static bool SupportsAudioCopy(string container, string? codec) => (container, codec) switch
    {
        (_, null) => true,
        ("webm", "opus" or "vorbis") => true,
        ("mov", "mp3") => true,
        ("mp4", "ac3" or "eac3" or "flac" or "wmav1" or "wmav2") => true,
        ("mov" or "mp4", "aac" or "pcm_u8" or "pcm_s16le" or "pcm_s16be" or "pcm_s24le" or "pcm_s24be"
            or "pcm_s32le" or "pcm_s32be") => true,
        _ => false
    };

    private static bool SupportsRotation(VideoDecodingModel video) =>
        video.FieldOrder == "progressive" && video.PixelFormat is { } pixels
        && (pixels.StartsWith("yuv420p", StringComparison.Ordinal) || pixels.StartsWith("yuv422p", StringComparison.Ordinal)
            || pixels is "yuvj420p" or "yuvj422p" or "nv12" or "nv16" or "uyvy422" or "yuyv422");

    private static string[] NormalizedVideoOptions(VideoDecodingModel video)
    {
        if (video.Properties.Width % 2 != 0 || video.Properties.Height % 2 != 0)
            throw new PhotoContentInvalidException();

        var filter = video.FieldOrder == "progressive" ? "setfield=prog"
            : "bwdif=mode=send_frame:parity=auto:deint=interlaced,setfield=prog";
        return ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast", "-crf", "14",
            "-fps_mode", "passthrough", "-enc_time_base:v", "demux", "-vf", filter];
    }

    private static string[] NormalizedAudioOptions(string format, long sourceBytes) => format switch
    {
        "mov" when sourceBytes <= MaximumInputBytes - 60_000_000 => ["-c:a", "pcm_s16le"],
        "webm" => ["-c:a", "libopus", "-b:a", "128k"],
        _ => ["-c:a", "aac", "-profile:a", "aac_low", "-b:a", "192k"]
    };
}
