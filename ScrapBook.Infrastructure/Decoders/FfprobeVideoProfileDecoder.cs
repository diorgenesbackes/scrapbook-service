using System.Globalization;
using System.Text.Json;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using static ScrapBook.Infrastructure.Decoders.FfprobeVideoDecoder;

namespace ScrapBook.Infrastructure.Decoders;

internal sealed class FfprobeVideoProfileDecoder(string executablePath)
{
    private const int MaximumMetadataOutput = 64 * 1024;
    private const int MaximumPixels = 33_177_600;

    public async Task<VideoDecodingModel> DecodeAsync(string path, bool source, bool playback,
        CancellationToken cancellationToken)
    {
        try
        {
            var webm = await VideoContainerReader.IsWebmAsync(path, cancellationToken);
            if (playback && webm) throw new PhotoContentInvalidException();

            using var metadata = await ReadMetadataAsync(path, webm, cancellationToken);
            var format = metadata.RootElement.GetProperty("format");
            var tracks = metadata.RootElement.GetProperty("streams");
            var (video, audio) = SelectPrimaryTracks(tracks);
            var iso = webm ? default : await IsoVideoContainerReader.InspectAsync(path, !source, playback, cancellationToken);
            var (mediaType, container) = Container(Text(format, "format_name"), webm, iso.MajorBrand);
            var videoIndex = RequiredInteger(video, "index", 0);
            var videoCodec = RequireCodec(video);
            var audioIndex = audio.ValueKind == JsonValueKind.Object ? RequiredInteger(audio, "index", 0) : (int?)null;
            var audioCodec = audioIndex is not null ? RequireCodec(audio) : null;
            var audioProfile = audioIndex is not null ? Text(audio, "profile") : null;
            var channels = audioIndex is not null ? RequiredInteger(audio, "channels", 1) : 0;
            var sampleRate = audioIndex is not null ? PositiveNumber(Text(audio, "sample_rate")) : 0;
            var width = RequiredInteger(video, "width", 1);
            var height = RequiredInteger(video, "height", 1);
            if ((long)width * height > MaximumPixels) throw new PhotoDecoderBudgetException();

            var aspect = PixelAspect(video);
            var rotation = VideoDisplayMatrixReader.Rotation(video);
            var hasLocation = iso.HasLocation || MediaLocationMetadata.ContainsLocation(format)
                || tracks.EnumerateArray().Any(MediaLocationMetadata.ContainsLocation);

            if (!source && !webm && tracks.GetArrayLength() != iso.TrackCount) throw new PhotoContentInvalidException();

            var completePresentation = tracks.GetArrayLength() == (audioIndex is null ? 1 : 2);
            if (playback && (container != "mp4" || videoCodec != "h264" || hasLocation
                || !completePresentation
                || audioIndex is not null && (audioCodec != "aac" || audioProfile != "LC")))
                throw new PhotoContentInvalidException();

            var packets = new VideoPacketTimeline(videoIndex, TimeBase(video), audioIndex,
                audioIndex is not null ? TimeBase(audio) : null, sampleRate);
            await ReadPacketsAsync(path, webm, packets, cancellationToken);
            var nominal = MediaRationalModel.Parse(Text(video, "r_frame_rate"), '/');
            var average = MediaRationalModel.Parse(Text(video, "avg_frame_rate"), '/');
            var (frameRate, variable, measuredDuration) = packets.Measure(nominal, average, webm, source);
            var duration = Math.Max(measuredDuration, Math.Max(Duration(video), Duration(audio)));
            if (!webm && completePresentation) duration = Math.Max(duration, Duration(format));
            if (duration <= 0 || duration > (source ? 300m : 300.250m)) throw new PhotoContentInvalidException();

            await RequireDecodedFrameAsync(path, webm, videoIndex, "video", cancellationToken);
            if (audioIndex is { } track)
                await RequireDecodedFrameAsync(path, webm, track, "audio", cancellationToken);

            var properties = new MediaVideoPropertiesModel(container, videoCodec, audioCodec, duration, width, height,
                aspect.Numerator, aspect.Denominator, frameRate.Numerator, frameRate.Denominator, rotation, variable,
                channels, sampleRate, hasLocation, audioProfile);
            return new VideoDecodingModel(properties, mediaType, videoIndex, audioIndex, tracks.GetArrayLength(),
                packets.VideoPacketCount);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or OverflowException or EndOfStreamException)
        {
            throw new PhotoContentInvalidException();
        }
    }

    private async Task<JsonDocument> ReadMetadataAsync(string path, bool webm, CancellationToken cancellationToken)
    {
        const string entries = "format=format_name,duration:format_tags:stream=index,codec_type,codec_name,width,height,"
            + "duration,time_base,sample_rate,channels,profile,sample_aspect_ratio,avg_frame_rate,r_frame_rate:stream_tags"
            + ":stream_disposition=attached_pic:stream_side_data=side_data_type,rotation,displaymatrix";
        var input = InputOptions(webm);
        string[] arguments = [.. input, "-show_format", "-show_streams", "-show_entries", entries,
            "-output_format", "json", "-i", path];
        var result = await MediaDecoderProcess.RunAsync(executablePath, path, arguments,
            (reader, token) => MediaDecoderProcess.ReadBoundedAsync(reader, MaximumMetadataOutput, token), cancellationToken);
        return JsonDocument.Parse(result, new JsonDocumentOptions { MaxDepth = 12 });
    }

    private Task<VideoPacketTimeline> ReadPacketsAsync(string path, bool webm, VideoPacketTimeline packets,
        CancellationToken cancellationToken)
    {
        var input = InputOptions(webm);
        string[] arguments = [.. input, "-show_packets", "-show_entries",
            "packet=stream_index,pts,duration,flags:packet_side_data=skip_samples,discard_padding",
            "-output_format", "compact=p=1:nk=0", "-i", path];
        return MediaDecoderProcess.RunAsync(executablePath, path, arguments, packets.ReadAsync, cancellationToken);
    }

    private async Task RequireDecodedFrameAsync(string path, bool webm, int index, string mediaType,
        CancellationToken cancellationToken)
    {
        var input = InputOptions(webm);
        string[] limitDecode = mediaType == "video" ? ["-skip_frame", "nokey"] : [];
        string[] arguments = [.. input, .. limitDecode, "-select_streams", index.ToString(CultureInfo.InvariantCulture),
            "-read_intervals", "%+#256", "-show_frames", "-show_entries", "frame=media_type,stream_index:frame_side_data=",
            "-output_format", "compact=p=1:nk=0", "-i", path];
        var result = await MediaDecoderProcess.RunAsync(executablePath, path, arguments,
            (reader, token) => MediaDecoderProcess.ReadBoundedAsync(reader, MaximumMetadataOutput, token), cancellationToken);
        var output = result.AsSpan();
        foreach (var line in output.Split('\n'))
        {
            var frame = output[line].TrimEnd();
            if (frame.StartsWith("frame|", StringComparison.Ordinal)
                && PacketField(frame, "media_type=").SequenceEqual(mediaType)
                && int.TryParse(PacketField(frame, "stream_index="), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var stream) && stream == index) return;
        }

        throw new PhotoContentInvalidException();
    }

    internal static string[] InputOptions(bool webm) => MediaDecoderProcess.InputOptions(webm ? "matroska,webm" : "mov", !webm);

    private static (JsonElement Video, JsonElement Audio) SelectPrimaryTracks(JsonElement tracks)
    {
        var video = tracks.EnumerateArray().FirstOrDefault(track => Text(track, "codec_type") == "video" && !IsAttachedPicture(track));
        var audio = tracks.EnumerateArray().FirstOrDefault(track => Text(track, "codec_type") == "audio");
        return video.ValueKind == JsonValueKind.Object ? (video, audio) : throw new PhotoContentInvalidException();
    }

    private static MediaRationalModel TimeBase(JsonElement track) =>
        MediaRationalModel.Parse(Text(track, "time_base"), '/') ?? throw new PhotoContentInvalidException();

    private static MediaRationalModel PixelAspect(JsonElement track)
    {
        var aspect = Text(track, "sample_aspect_ratio");
        return aspect is null or "N/A" or "0:1" ? new MediaRationalModel(1, 1)
            : MediaRationalModel.Parse(aspect, ':') ?? throw new PhotoContentInvalidException();
    }

    private static int PositiveNumber(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new PhotoContentInvalidException();

    private static decimal Duration(JsonElement item) => item.ValueKind == JsonValueKind.Object
        && decimal.TryParse(Text(item, "duration"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
        ? value : 0;
}
