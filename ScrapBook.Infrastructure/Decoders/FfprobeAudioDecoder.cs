using System.Globalization;
using System.Text.Json;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

public sealed class FfprobeAudioDecoder(string executablePath)
{
    private const int MaximumMetadataOutput = 24 * 1024;
    private const int MaximumMediaLines = 500_000;
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> IsoBrands = new(StringComparer.Ordinal)
    {
        "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "iso7", "iso8", "iso9",
        "mp41", "mp42", "avc1", "dash", "M4A ", "M4V ", "MSNV"
    };

    internal async Task<AudioMetadataModel> DecodeAsync(string path, byte[] sha256, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(executablePath)) throw new PhotoDecoderUnavailableException();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DecodeTimeout);
        try
        {
            var webm = await VideoContainerReader.IsWebmAsync(path, budget.Token);
            var metadata = await ReadMetadataAsync(path, webm, budget.Token);
            using var json = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 10 });
            var (mediaType, container) = Container(json.RootElement.GetProperty("format"), webm);
            var tracks = AudioTracks(json.RootElement.GetProperty("streams"), container);
            if (container == "ogg") await OggPageReader.RequireCompleteAsync(path, budget.Token);
            if (!await ReadFramesAsync(path, container, tracks, budget.Token))
                throw new PhotoContentInvalidException();
            await VerifyDecodeAsync(path, container, budget.Token);
            var duration = tracks.Values.Max(track => track.Duration(webm));
            var primary = tracks.Values.FirstOrDefault(track => track.IsDefault) ?? tracks.Values.First();
            return new AudioMetadataModel(mediaType, container, primary.Codec, duration, sha256);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhotoDecoderBudgetException();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or OverflowException)
        {
            throw new PhotoContentInvalidException();
        }
    }

    private Task<string> ReadMetadataAsync(string path, bool webm, CancellationToken cancellationToken)
    {
        var formats = webm ? "matroska,webm" : "mp3,mov,wav,ogg";
        var input = AudioDecoderProcess.InputOptions(formats, false);
        string[] options = ["-count_packets", "-show_streams", "-show_format", "-show_entries",
            "format=format_name:format_tags=major_brand,compatible_brands:stream=index,codec_type,codec_name,"
            + "sample_rate,time_base,nb_read_packets:stream_tags=:stream_disposition=default,attached_pic",
            "-output_format", "json", "-i", path];
        return AudioDecoderProcess.RunAsync(executablePath, path, [.. input, .. options],
            (reader, token) => AudioDecoderProcess.ReadBoundedAsync(reader, MaximumMetadataOutput, token), cancellationToken);
    }

    private Task<bool> ReadFramesAsync(string path, string container, Dictionary<int, AudioTrackTimingModel> tracks,
        CancellationToken cancellationToken)
    {
        var input = AudioDecoderProcess.InputOptions(FormatWhitelist(container), container == "m4a");
        var mp3 = container == "mp3";
        string[] packetOptions = mp3 ? ["-show_packets"] : [];
        const string frameEntries = "frame=stream_index,best_effort_timestamp,nb_samples:frame_side_data=";
        var entries = mp3 ? frameEntries + ":packet=pos,size,flags:packet_side_data=" : frameEntries;
        string[] options = ["-select_streams", "a", "-show_frames", .. packetOptions, "-show_entries", entries,
            "-output_format", "compact=p=1:nk=0", "-i", path];
        return AudioDecoderProcess.RunAsync(executablePath, path, [.. input, .. options],
            (reader, token) => CaptureFramesAsync(reader, tracks, mp3 ? path : null, token), cancellationToken);
    }

    private Task<string> VerifyDecodeAsync(string path, string container, CancellationToken cancellationToken)
    {
        var ffmpeg = Path.Combine(Path.GetDirectoryName(executablePath)!, "ffmpeg");
        var input = AudioDecoderProcess.InputOptions(FormatWhitelist(container), container == "m4a");
        string[] options = ["-hide_banner", "-nostdin", "-nostats", "-xerror", "-max_error_rate", "0",
            "-filter_threads", "1", .. input, "-i", path, "-map", "0:a", "-threads", "1", "-f", "null", "-"];
        return AudioDecoderProcess.RunAsync(ffmpeg, path, options,
            (reader, token) => AudioDecoderProcess.ReadBoundedAsync(reader, 1024, token), cancellationToken);
    }

    private static string FormatWhitelist(string container) => container switch
    {
        "m4a" => "mov",
        "webm" => "matroska,webm",
        _ => container
    };

    private static (string MediaType, string Container) Container(JsonElement format, bool webm) =>
        (Text(format, "format_name"), webm) switch
        {
            ("mp3", false) => ("audio/mpeg", "mp3"),
            ("mov,mp4,m4a,3gp,3g2,mj2", false) when IsM4a(format) => ("audio/mp4", "m4a"),
            ("wav", false) => ("audio/wav", "wav"),
            ("ogg", false) => ("audio/ogg", "ogg"),
            ("matroska,webm", true) => ("audio/webm", "webm"),
            _ => throw new PhotoContentInvalidException()
        };

    private static bool IsM4a(JsonElement format)
    {
        if (!format.TryGetProperty("tags", out var tags) || Text(tags, "major_brand") is not { Length: 4 } major
            || major == "qt  ") return false;

        var compatible = Text(tags, "compatible_brands").AsSpan();
        var known = IsoBrands.Contains(major);
        if (IsImageBrand(major) || compatible.Length % 4 != 0) return false;
        for (var offset = 0; offset < compatible.Length; offset += 4)
        {
            var brand = compatible.Slice(offset, 4);
            if (IsImageBrand(brand)) return false;
            known |= IsoBrands.GetAlternateLookup<ReadOnlySpan<char>>().Contains(brand);
        }

        return known;
    }

    private static bool IsImageBrand(ReadOnlySpan<char> brand) =>
        brand is "avif" or "avis" or "mif1" or "msf1" || brand[..3] is "hei" or "hev";

    private static Dictionary<int, AudioTrackTimingModel> AudioTracks(JsonElement streams, string container)
    {
        var tracks = new Dictionary<int, AudioTrackTimingModel>();
        foreach (var stream in streams.EnumerateArray())
        {
            var type = Text(stream, "codec_type");
            var codec = Text(stream, "codec_name");
            var disposition = stream.GetProperty("disposition");
            if (type == "video" && Number(disposition, "attached_pic") == 1
                && codec is "mjpeg" or "png" or "bmp" or "webp"
                && Text(stream, "nb_read_packets") == "1") continue;
            if (type != "audio" || !IsKnownCodec(codec) || container == "mp3" && codec != "mp3"
                || Number(stream, "index") is not >= 0)
                throw new PhotoContentInvalidException();

            var index = Number(stream, "index")!.Value;
            if (!TryPositiveInteger(Text(stream, "sample_rate"), out var sampleRate))
                throw new PhotoContentInvalidException();
            var timeBase = Text(stream, "time_base").AsSpan();
            var separator = timeBase.IndexOf('/');
            if (separator <= 0 || !TryPositiveInteger(timeBase[..separator], out var numerator)
                || !TryPositiveInteger(timeBase[(separator + 1)..], out var denominator)
                || !tracks.TryAdd(index, new AudioTrackTimingModel(codec!, Number(disposition, "default") == 1,
                    sampleRate, numerator, denominator)))
                throw new PhotoContentInvalidException();
        }

        return tracks.Count > 0 ? tracks : throw new PhotoContentInvalidException();
    }

    private static bool IsKnownCodec(string? codec) => codec is { Length: > 0 and <= 64 } and not ("unknown" or "none")
        && codec.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static async Task<bool> CaptureFramesAsync(StreamReader reader, Dictionary<int, AudioTrackTimingModel> tracks,
        string? mp3Path, CancellationToken cancellationToken)
    {
        await using var mp3 = mp3Path is null ? null : new FileStream(mp3Path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[4];
        var count = 0;
        var hasFrames = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (++count > MaximumMediaLines) throw new PhotoDecoderBudgetException();
            if (mp3 is not null && line.StartsWith("packet|", StringComparison.Ordinal))
                await VerifyMp3PacketAsync(line, mp3, header, cancellationToken);
            else
            {
                RecordFrame(line, tracks);
                hasFrames = true;
            }
        }

        return hasFrames;
    }

    private static Task VerifyMp3PacketAsync(string packet, FileStream file, Memory<byte> header,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(Field(packet, "pos="), NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            || !TryPositiveInteger(Field(packet, "size="), out var size)
            || Field(packet, "flags=").Contains('C'))
            throw new PhotoContentInvalidException();

        return Mp3FrameReader.RequireCompleteAsync(file, position, size, header, cancellationToken);
    }

    private static void RecordFrame(ReadOnlySpan<char> frame, Dictionary<int, AudioTrackTimingModel> tracks)
    {
        if (!frame.StartsWith("frame|", StringComparison.Ordinal)
            || !int.TryParse(Field(frame, "stream_index="), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || !tracks.TryGetValue(index, out var track)
            || !TryPositiveInteger(Field(frame, "nb_samples="), out var samples))
            throw new PhotoContentInvalidException();

        long? timestamp = long.TryParse(Field(frame, "best_effort_timestamp="), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var position) ? position : null;
        track.AddFrame(samples, timestamp);
    }

    private static ReadOnlySpan<char> Field(ReadOnlySpan<char> frame, string name)
    {
        foreach (var range in frame.Split('|'))
        {
            var field = frame[range];
            if (field.StartsWith(name, StringComparison.Ordinal)) return field[name.Length..];
        }

        return [];
    }

    private static bool TryPositiveInteger(ReadOnlySpan<char> value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private static int? Number(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var result) ? result : null;

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
