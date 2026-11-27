using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Decoders;
using ScrapBook.Infrastructure.Storage;
using ScrapBook.UnitTests.Decoders;
using ScrapBook.UnitTests.Services;
using static ScrapBook.UnitTests.Services.MediaAudioTestFixture;

namespace ScrapBook.UnitTests.Storage;

public sealed class S3MediaVideoStorageTests
{
    private const string SourceKey = "submissions/original/701/confirmed-video";
    private static string InputKey => $"submissions/transcode-input/701/{RequestKey:N}/input";
    private static string VideoKey => $"submissions/transcode-output/701/{RequestKey:N}/playback.mp4";

    [FfmpegTheory]
    [InlineData("video-rotated-location.mov", "video/quicktime", "h264", 64, 48, 90, false, 0)]
    [InlineData("video-variable.webm", "video/webm", "vp9", 64, 48, 0, true, 0)]
    [InlineData("video-fragmented.mp4", "video/mp4", "h264", 32, 32, 0, false, 1)]
    public async Task StagesOnlyThePinnedOriginalAtAnImmutableAttemptKeyAsync(string fixture, string mediaType,
        string codec, int width, int height, int rotation, bool variable, int channels)
    {
        using var cancellation = new CancellationTokenSource();
        var bytes = await ReadFixtureAsync(fixture, cancellation.Token);
        var source = Source(bytes);
        using var client = new MediaAudioS3ClientStub(SourceKey, bytes) { SourceMediaType = mediaType };
        var storage = new S3MediaVideoStorage(client, Decoder());

        var result = await storage.StageAsync(source, InputKey, cancellation.Token);

        var original = Assert.Single(client.Reads, read => read.Request.Key == SourceKey).Request;
        Assert.Equal((Bucket, source.VersionId), (original.BucketName, original.VersionId));
        var (put, staged, _) = Assert.Single(client.Puts);
        Assert.Equal((Bucket, InputKey, "*", mediaType), (put.BucketName, put.Key, put.IfNoneMatch, put.ContentType));
        Assert.Equal(source.VersionId, put.Metadata["source-version"]);
        Assert.Equal(Convert.ToBase64String(source.Sha256), put.Metadata["source-sha256"]);
        var hash = SHA256.HashData(staged);
        Assert.Equal(Convert.ToBase64String(hash), put.ChecksumSHA256);
        var stagedRead = Assert.Single(client.Reads, read => read.Request.Key == InputKey).Request;
        Assert.Equal((Bucket, client.WrittenVersion), (stagedRead.BucketName, stagedRead.VersionId));
        Assert.Equal((Bucket, InputKey, client.WrittenVersion, (long)staged.Length),
            (result.Object.Bucket, result.Object.Key, result.Object.VersionId, result.Object.Size));
        Assert.Equal(hash, result.Object.Sha256);
        Assert.Equal((codec, width, height, rotation, variable, channels),
            (result.Properties.VideoCodec, result.Properties.Width, result.Properties.Height, result.Properties.Rotation,
                result.Properties.VariableFrameRate, result.Properties.AudioChannels));
        Assert.False(result.Properties.HasLocationMetadata);
        if (variable)
        {
            var framesPerSecond = (decimal)result.Properties.FrameRateNumerator / result.Properties.FrameRateDenominator;
            Assert.InRange(framesPerSecond, 14.9m, 15m);
        }
    }

    [FfmpegTheory]
    [InlineData("video-primary-2s-secondary-4s.mp4")]
    public async Task StagesThePrimaryAudioWithoutInheritingTheLongerSecondaryTrackAsync(string fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = await ReadFixtureAsync(fixture, deadline.Token);
        using var client = new MediaAudioS3ClientStub(SourceKey, bytes) { SourceMediaType = "video/mp4" };
        var storage = new S3MediaVideoStorage(client, Decoder());

        var input = await storage.StageAsync(Source(bytes), InputKey, deadline.Token);

        Assert.Equal(2m, input.Properties.Duration);
        Assert.Equal(("aac", 1), (input.Properties.AudioCodec, input.Properties.AudioChannels));
        using var probe = await ProbeStagedStreamsAsync(Assert.Single(client.Puts).Bytes, deadline.Token);
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Assert.Equal(new[] { "video", "audio" }, streams.Select(stream => stream.GetProperty("codec_type").GetString()));
        Assert.Equal("2.000000", streams[0].GetProperty("duration").GetString());
        Assert.Equal("2.000000", streams[1].GetProperty("duration").GetString());
        Assert.Equal(1, streams[1].GetProperty("channels").GetInt32());
    }

    [FfmpegTheory]
    [InlineData("video-rotated-444.mov")]
    [InlineData("video-rotated-interlaced.mov")]
    public async Task StagesRotatedH264InProgressiveYuv420WithoutChangingTheSourceGeometryOrCadenceAsync(string fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = await ReadFixtureAsync(fixture, deadline.Token);
        using var client = new MediaAudioS3ClientStub(SourceKey, bytes) { SourceMediaType = "video/quicktime" };
        var storage = new S3MediaVideoStorage(client, Decoder());

        var input = await storage.StageAsync(Source(bytes), InputKey, deadline.Token);

        var properties = input.Properties;
        Assert.Equal(("h264", 96, 64, 90, 24, 1),
            (properties.VideoCodec, properties.Width, properties.Height, properties.Rotation,
                properties.FrameRateNumerator, properties.FrameRateDenominator));
        Assert.False(properties.VariableFrameRate);
        using var probe = await ProbeStagedStreamsAsync(Assert.Single(client.Puts).Bytes, deadline.Token);
        var video = Assert.Single(probe.RootElement.GetProperty("streams").EnumerateArray());
        Assert.Equal("h264", video.GetProperty("codec_name").GetString());
        Assert.Equal("yuv420p", video.GetProperty("pix_fmt").GetString());
        Assert.Equal("progressive", video.GetProperty("field_order").GetString());
        Assert.Equal((96, 64), (video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32()));
        Assert.Equal("24/1", video.GetProperty("avg_frame_rate").GetString());
        var rotation = Assert.Single(video.GetProperty("side_data_list").EnumerateArray(),
            item => item.TryGetProperty("rotation", out _));
        Assert.Equal(90, rotation.GetProperty("rotation").GetInt32());
    }

    [FfmpegTheory]
    [InlineData("video-webm-opus-300s.webm")]
    public async Task StagesAConfirmedFiveMinuteVideoAfterApplyingOpusPreskipAsync(string fixture)
    {
        var bytes = await ReadFixtureAsync(fixture, CancellationToken.None);
        using var client = new MediaAudioS3ClientStub(SourceKey, bytes) { SourceMediaType = "video/webm" };
        var storage = new S3MediaVideoStorage(client, Decoder());

        var input = await storage.StageAsync(Source(bytes), InputKey, CancellationToken.None);

        Assert.Equal(300m, input.Properties.Duration);
        Assert.Equal(("vp9", "opus"), (input.Properties.VideoCodec, input.Properties.AudioCodec));
        Assert.Single(client.Puts);
    }

    [FfmpegTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalStageRetryReusesOnlyAnInputBoundToTheSameSourceAsync(bool foreignSource)
    {
        var bytes = await ReadFixtureAsync("video-variable.webm", CancellationToken.None);
        var source = Source(bytes);
        using var client = new MediaAudioS3ClientStub(SourceKey, bytes) { SourceMediaType = "video/webm", ConflictOnPut = true };
        var storage = new S3MediaVideoStorage(client, Decoder());
        if (foreignSource)
            client.ExistingSourceHash = Convert.ToBase64String(SHA256.HashData([1, 2, 3]));

        if (foreignSource)
            await Assert.ThrowsAsync<MediaOutputInvalidException>(() => storage.StageAsync(source, InputKey, CancellationToken.None));
        else
            Assert.Equal(client.WrittenVersion, (await storage.StageAsync(source, InputKey, CancellationToken.None)).Object.VersionId);

        Assert.Single(client.Puts);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("size")]
    [InlineData("hash")]
    public async Task ASourceOutsideTheConfirmationScopeCannotBeStagedAsync(string difference)
    {
        byte[] bytes = [1, 2, 3, 4];
        var source = Source(bytes);
        using var client = new MediaAudioS3ClientStub(SourceKey, bytes);
        var storage = new S3MediaVideoStorage(client, new VideoTranscodingDecoder("/unused/ffprobe"));

        if (difference == "key")
        {
            await Assert.ThrowsAsync<MediaOutputInvalidException>(() =>
                storage.StageAsync(source, $"submissions/transcode-input/702/{RequestKey:N}/input", CancellationToken.None));
            Assert.Empty(client.Reads);
        }
        else
        {
            if (difference == "version")
                client.ReadVersion = "unconfirmed-version";
            if (difference == "size")
                source = source with { Size = bytes.Length + 1 };
            if (difference == "hash")
            {
                var otherHash = SHA256.HashData([4, 3, 2, 1]);
                source = source with { Sha256 = otherHash, OriginalSha256 = [.. otherHash] };
            }

            if (difference == "version")
                await Assert.ThrowsAsync<MediaVariantStorageException>(() => storage.StageAsync(source, InputKey, CancellationToken.None));
            else
                await Assert.ThrowsAsync<MediaSourceIntegrityException>(() => storage.StageAsync(source, InputKey, CancellationToken.None));
            Assert.Equal(source.VersionId, Assert.Single(client.Reads).Request.VersionId);
        }

        Assert.Empty(client.Puts);
    }

    [FfmpegTheory]
    [InlineData("video-muted.mp4", null, null, 0, 0)]
    [InlineData("audio-with-video.mp4", "aac", "LC", 1, 48000)]
    public async Task VerifiesTheVersionAndActualTracksOfTheMp4PlaybackAsync(string fixture, string? audioCodec,
        string? audioProfile, int channels, int sampleRate)
    {
        using var cancellation = new CancellationTokenSource();
        var bytes = await ReadFixtureAsync(fixture, cancellation.Token);
        using var client = new MediaAudioS3ClientStub(VideoKey, bytes) { SourceVersion = "output-version", SourceMediaType = "video/mp4" };
        var storage = new S3MediaVideoStorage(client, Decoder());

        var output = await storage.VerifyAsync(Bucket, VideoKey, cancellation.Token);

        var video = output.Video;
        Assert.NotNull(video);
        Assert.Equal(("video/mp4", "mp4", "h264"), (output.MediaType, output.Container, video.VideoCodec));
        Assert.Equal((audioCodec, audioProfile, channels, sampleRate),
            (output.AudioCodec, video.AudioProfile, output.Channels, output.SampleRate));
        Assert.False(video.HasLocationMetadata);
        Assert.False(video.VariableFrameRate);
        Assert.Equal(0, video.Rotation);
        Assert.Equal((Bucket, VideoKey, "output-version", (long)bytes.Length),
            (output.Object.Bucket, output.Object.Key, output.Object.VersionId, output.Object.Size));
        Assert.Equal(SHA256.HashData(bytes), output.Object.Sha256);
        var head = Assert.Single(client.Heads).Request;
        Assert.Equal((Bucket, VideoKey), (head.BucketName, head.Key));
        var read = Assert.Single(client.Reads).Request;
        Assert.Equal((Bucket, VideoKey, "output-version"), (read.BucketName, read.Key, read.VersionId));
        Assert.Empty(client.Puts);
    }

    [FfmpegTheory]
    [InlineData("video-variable.webm")]
    [InlineData("audio-aac.m4a")]
    [InlineData("video-location.mp4")]
    public async Task RejectsPlaybackWithAnUnexpectedContainerMissingVideoOrLocationMetadataAsync(string fixture)
    {
        var bytes = await ReadFixtureAsync(fixture, CancellationToken.None);
        using var client = new MediaAudioS3ClientStub(VideoKey, bytes) { SourceMediaType = "video/mp4" };
        var storage = new S3MediaVideoStorage(client, Decoder());

        await Assert.ThrowsAsync<MediaOutputInvalidException>(() => storage.VerifyAsync(Bucket, VideoKey, CancellationToken.None));

        Assert.Single(client.Reads);
        Assert.Empty(client.Puts);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("expected-version", true)]
    public async Task RejectsMissingOrChangedPlaybackVersionsAsync(string headVersion, bool readsObject)
    {
        using var client = new MediaAudioS3ClientStub(VideoKey, [1, 2, 3])
        {
            SourceMediaType = "video/mp4",
            HeadVersion = headVersion,
            ReadVersion = "different-version"
        };
        var storage = new S3MediaVideoStorage(client, new VideoTranscodingDecoder("/unused/ffprobe"));

        await Assert.ThrowsAsync<MediaVariantStorageException>(() => storage.VerifyAsync(Bucket, VideoKey, CancellationToken.None));

        Assert.Single(client.Heads);
        if (readsObject)
            Assert.Equal(headVersion, Assert.Single(client.Reads).Request.VersionId);
        else
            Assert.Empty(client.Reads);
    }

    private static async Task<JsonDocument> ProbeStagedStreamsAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var scratch = Directory.CreateTempSubdirectory("scrapbook-staged-video-test-");
        try
        {
            var path = Path.Combine(scratch.FullName, "staged-video");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            var start = new ProcessStartInfo(FfmpegTheoryAttribute.FfprobePath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string[] arguments = ["-v", "error", "-show_entries",
                "stream=codec_type,codec_name,channels,duration,pix_fmt,field_order,width,height,avg_frame_rate:stream_side_data=rotation",
                "-of", "json", path];
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFprobe.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errors = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                Assert.True(process.ExitCode == 0, await errors);
                return JsonDocument.Parse(await output);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill();
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static VideoTranscodingDecoder Decoder() => new(FfmpegTheoryAttribute.FfprobePath!);

    private static MediaSourceEntity Source(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return MediaPreparationTestFixture.Source() with
        {
            Kind = "video",
            ObjectKey = SourceKey,
            Size = bytes.Length,
            Sha256 = hash,
            OriginalSha256 = [.. hash]
        };
    }

    private static Task<byte[]> ReadFixtureAsync(string name, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Decoders", "Fixtures", name), cancellationToken);
}
