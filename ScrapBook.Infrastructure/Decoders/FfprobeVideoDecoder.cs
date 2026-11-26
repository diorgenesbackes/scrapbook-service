using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

public sealed class FfprobeVideoDecoder(string executablePath)
{
    private const int MaximumMetadataOutput = 24 * 1024;
    private const int MaximumPacketOutput = 4 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> Mp4Brands = new(StringComparer.Ordinal)
    {
        "isom", "iso1", "iso2", "iso3", "iso4", "iso5", "iso6", "iso7", "iso8", "iso9",
        "mp41", "mp42", "mp71", "avc1", "hvc1", "hev1", "av01", "M4V", "M4VH", "M4VP", "MSNV", "dash", "cmfc", "cmfs"
    };

    internal async Task<VideoMetadataModel> DecodeAsync(string path, byte[] sha256, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ProbeTimeout);
        try
        {
            var webm = await VideoContainerReader.IsWebmAsync(path, budget.Token);
            var metadata = await ProbeAsync(path, webm, MaximumMetadataOutput,
                ["-show_format", "-show_streams", "-show_entries",
                    "format=format_name,duration:format_tags=major_brand:stream=index,codec_type,codec_name,width,height,"
                    + "duration,time_base,sample_rate,avg_frame_rate,r_frame_rate:stream_disposition=attached_pic:stream_side_data=rotation",
                    "-output_format", "json"], budget.Token);
            using var json = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 10 });
            var streams = json.RootElement.GetProperty("streams");
            var format = json.RootElement.GetProperty("format");
            var primaryVideo = streams.EnumerateArray().FirstOrDefault(stream =>
                Text(stream, "codec_type") == "video" && !IsAttachedPicture(stream));
            if (primaryVideo.ValueKind != JsonValueKind.Object) throw new PhotoContentInvalidException();
            var index = RequiredInteger(primaryVideo, "index", 0);
            var width = RequiredInteger(primaryVideo, "width", 1);
            var height = RequiredInteger(primaryVideo, "height", 1);
            var codec = RequireCodec(primaryVideo);
            var audio = streams.EnumerateArray().FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
            var audioCodec = audio.ValueKind == JsonValueKind.Object ? RequireCodec(audio) : null;
            var audioIndex = audioCodec is null ? (int?)null : RequiredInteger(audio, "index", 0);
            var (numerator, denominator) = FrameRate(primaryVideo);
            var (mediaType, container) = Container(format, webm);
            var sampleRate = int.TryParse(Text(audio, "sample_rate"), NumberStyles.None, CultureInfo.InvariantCulture,
                out var parsedSampleRate) && parsedSampleRate > 0 ? parsedSampleRate : 0;
            var measuredDuration = await MeasurePacketDurationAsync(path, webm, index, audioIndex, sampleRate,
                (decimal)denominator / numerator, WebmTimestampStep(primaryVideo, webm), budget.Token);
            var duration = Math.Max(Duration(primaryVideo), measuredDuration);
            if (!webm) duration = Math.Max(duration, Math.Max(Duration(format), Duration(audio)));

            await RequireDecodedFrameAsync(path, webm, index, budget.Token);
            return new VideoMetadataModel(mediaType, container, codec, audioCodec, duration, width, height,
                numerator, denominator, Rotation(primaryVideo), sha256);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhotoDecoderBudgetException();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw new PhotoContentInvalidException();
        }
    }

    private async Task RequireDecodedFrameAsync(string path, bool webm, int index, CancellationToken cancellationToken)
    {
        var result = await ProbeAsync(path, webm, MaximumMetadataOutput,
            ["-select_streams", index.ToString(CultureInfo.InvariantCulture), "-skip_frame", "nokey",
                "-read_intervals", "%+#256", "-show_frames", "-show_entries", "frame=media_type",
                "-output_format", "compact=p=0:nk=1"], cancellationToken);
        if (!result.AsSpan().Contains("video", StringComparison.Ordinal)) throw new PhotoContentInvalidException();
    }

    private async Task<decimal> MeasurePacketDurationAsync(string path, bool webm, int video, int? audio,
        int sampleRate, decimal frameDuration, decimal timestampStep, CancellationToken cancellationToken)
    {
        var result = await ProbeAsync(path, webm, MaximumPacketOutput, ["-show_packets", "-show_entries",
            "packet=stream_index,pts_time,duration_time,flags:packet_side_data=skip_samples,discard_padding",
            "-output_format", "compact=p=1:nk=0"], cancellationToken);
        decimal? videoFirst = null;
        decimal? videoLast = null;
        decimal? audioFirst = null;
        decimal? audioLast = null;
        var output = result.AsSpan();
        foreach (var line in output.Split('\n'))
        {
            var packet = output[line].TrimEnd();
            if (!packet.StartsWith("packet|", StringComparison.Ordinal)) continue;
            var streamText = PacketField(packet, "stream_index=");
            var flags = PacketField(packet, "flags=");
            if (flags.IndexOf('C') >= 0) throw new PhotoContentInvalidException();
            if (flags.IndexOf('D') >= 0 || !int.TryParse(streamText, NumberStyles.None, CultureInfo.InvariantCulture, out var stream)
                || stream != video && stream != audio || !TryDuration(PacketField(packet, "pts_time="), out var pts))
                continue;

            if (!TryDuration(PacketField(packet, "duration_time="), out var length) || length <= 0)
                length = stream == video ? frameDuration : 0;
            if (stream == video)
            {
                videoFirst = Math.Min(videoFirst ?? pts, pts);
                videoLast = Math.Max(videoLast ?? pts, pts + length);
            }
            else
            {
                RecordAudioPacket(packet, pts, length, sampleRate, ref audioFirst, ref audioLast);
            }
        }

        if (videoFirst is null || videoLast is null || videoLast <= videoFirst) throw new PhotoContentInvalidException();
        if (audioFirst is null || audioLast is null) return videoLast.Value - videoFirst.Value;
        if (webm)
        {
            audioFirst = Math.Max(0, audioFirst.Value);
            if (timestampStep > 0) audioLast = decimal.Floor(audioLast.Value / timestampStep) * timestampStep;
        }

        return Math.Max(videoLast.Value, audioLast.Value) - Math.Min(videoFirst.Value, audioFirst.Value);
    }

    private static void RecordAudioPacket(ReadOnlySpan<char> packet, decimal pts, decimal duration,
        int sampleRate, ref decimal? first, ref decimal? last)
    {
        var begin = pts;
        var end = pts + duration;
        if (sampleRate > 0)
        {
            begin += PacketSampleOffset(packet, "side_datum/skip_samples:skip_samples=", sampleRate);
            end -= PacketSampleOffset(packet, "side_datum/skip_samples:discard_padding=", sampleRate);
        }
        if (end <= begin) return;
        first = Math.Min(first ?? begin, begin);
        last = Math.Max(last ?? end, end);
    }

    private static decimal PacketSampleOffset(ReadOnlySpan<char> packet, string name, int sampleRate) =>
        int.TryParse(PacketField(packet, name), NumberStyles.None, CultureInfo.InvariantCulture, out var samples)
            ? (decimal)samples / sampleRate : 0;

    private static decimal WebmTimestampStep(JsonElement stream, bool webm)
    {
        if (!webm) return 0;
        var timeBase = Text(stream, "time_base").AsSpan();
        var separator = timeBase.IndexOf('/');
        return separator > 0 && int.TryParse(timeBase[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n > 0 && int.TryParse(timeBase[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0
            ? (decimal)n / d : 0;
    }

    private static ReadOnlySpan<char> PacketField(ReadOnlySpan<char> packet, string name)
    {
        foreach (var range in packet.Split('|'))
        {
            var field = packet[range];
            if (field.StartsWith(name, StringComparison.Ordinal)) return field[name.Length..];
        }
        return [];
    }

    private async Task<string> ProbeAsync(string path, bool webm, int limit, string[] options, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = StartInfo(path, webm, options) };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new PhotoDecoderUnavailableException();
        }
        catch (Win32Exception)
        {
            throw new PhotoDecoderUnavailableException();
        }

        process.StandardInput.Close();
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, limit, cancellationToken);
            var errors = ReadBoundedAsync(process.StandardError, MaximumMetadataOutput, cancellationToken);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(cancellationToken));
            var error = await errors;
            if (process.ExitCode is 137 or 152 || error.Contains("Cannot allocate memory", StringComparison.Ordinal))
                throw new PhotoDecoderBudgetException();
            if (process.ExitCode is >= 125 || error.Contains("Unrecognized option", StringComparison.Ordinal))
                throw new PhotoDecoderUnavailableException();
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error)) throw new PhotoContentInvalidException();
            return await output;
        }
        finally
        {
            await StopAsync(process);
        }
    }

    private ProcessStartInfo StartInfo(string path, bool webm, string[] options)
    {
        if (!Path.IsPathFullyQualified(executablePath)) throw new PhotoDecoderUnavailableException();
        var linux = OperatingSystem.IsLinux();
        var info = new ProcessStartInfo(linux ? "/usr/bin/prlimit" : executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (linux)
        {
            foreach (var argument in new[] { "--as=805306368", "--cpu=25", "--", executablePath })
                info.ArgumentList.Add(argument);
        }
        info.Environment.Clear();
        info.Environment["LC_ALL"] = "C";
        info.Environment["TMPDIR"] = info.WorkingDirectory;
        string[] common = ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", webm ? "matroska,webm" : "mov",
            "-max_streams", "16", "-max_alloc", "134217728", "-max_pixels", "33177600", "-threads", "1",
            "-probesize", "10000000", "-analyzeduration", "10000000", "-err_detect", "explode"];
        foreach (var argument in common) info.ArgumentList.Add(argument);
        if (!webm)
        {
            foreach (var argument in new[] { "-enable_drefs", "0", "-use_absolute_path", "0" }) info.ArgumentList.Add(argument);
        }
        foreach (var argument in options) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(path);
        return info;
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when ((error is InvalidOperationException or Win32Exception) && process.HasExited)
        {
        }
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[limit + 1];
        var length = await reader.ReadBlockAsync(buffer, cancellationToken);
        return length <= limit ? new string(buffer, 0, length) : throw new PhotoDecoderBudgetException();
    }

    private static (string MediaType, string Container) Container(JsonElement format, bool webm) =>
        (Text(format, "format_name"), webm) switch
        {
            ("matroska,webm", true) => ("video/webm", "webm"),
            ("mov,mp4,m4a,3gp,3g2,mj2", false) => IsoContainer(format),
            _ => throw new PhotoContentInvalidException()
        };

    private static (string MediaType, string Container) IsoContainer(JsonElement format)
    {
        var brand = format.TryGetProperty("tags", out var tags) ? Text(tags, "major_brand")?.TrimEnd() : null;
        if (brand is null or "qt") return ("video/quicktime", "mov");
        return Mp4Brands.Contains(brand) ? ("video/mp4", "mp4") : throw new PhotoContentInvalidException();
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, "attached_pic") == 1;

    private static string RequireCodec(JsonElement stream)
    {
        var codec = Text(stream, "codec_name");
        if (codec is not { Length: > 0 and <= 64 } or "unknown" or "none"
            || !codec.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            throw new PhotoContentInvalidException();
        return codec;
    }

    private static int? Integer(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var result) ? result : null;

    private static int RequiredInteger(JsonElement value, string name, int minimum) =>
        Integer(value, name) is { } result && result >= minimum ? result : throw new PhotoContentInvalidException();

    private static int? Rotation(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var sideData)) return null;
        foreach (var item in sideData.EnumerateArray())
        {
            if (Integer(item, "rotation") is not { } rotation) continue;
            if (rotation % 90 != 0) throw new PhotoContentInvalidException();
            return (rotation % 360 + 360) % 360;
        }
        return null;
    }

    private static (int Numerator, int Denominator) FrameRate(JsonElement stream)
    {
        foreach (var field in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            var rate = Text(stream, field).AsSpan();
            var separator = rate.IndexOf('/');
            if (separator > 0 && int.TryParse(rate[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                && int.TryParse(rate[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0)
                return (n, d);
        }

        throw new PhotoContentInvalidException();
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static decimal Duration(JsonElement value) =>
        TryDuration(Text(value, "duration"), out var parsed) && parsed > 0 ? parsed : 0;

    private static bool TryDuration(ReadOnlySpan<char> value, out decimal duration) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
            out duration);
}
