using System.Security.Cryptography;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Decoders;
using ScrapBook.Infrastructure.Storage;
using ScrapBook.UnitTests.Decoders;
using ScrapBook.UnitTests.Services;
using static ScrapBook.UnitTests.Services.MediaAudioTestFixture;

namespace ScrapBook.UnitTests.Storage;

public sealed class S3MediaAudioStorageTests
{
    private const string SourceKey = "submissions/original/701/confirmed-audio";
    private static string InputKey => $"submissions/transcode-input/701/{RequestKey:N}/input";

    [FfmpegTheory]
    [InlineData("audio-mp3.mp3", "audio/mpeg", 1, true)]
    [InlineData("audio-aac.m4a", "audio/mp4", 1, false)]
    [InlineData("audio-fragmented.m4a", "audio/mp4", 1, false)]
    [InlineData("audio-pcm.wav", "audio/wav", 1, true)]
    [InlineData("audio-vorbis.ogg", "audio/ogg", 2, true)]
    [InlineData("audio-opus.ogg", "audio/ogg", 1, true)]
    [InlineData("audio-opus.webm", "audio/ogg", 1, false)]
    [InlineData("audio-vorbis.webm", "audio/ogg", 2, false)]
    [InlineData("audio-default-second.m4a", "audio/mp4", 2, false)]
    [InlineData("audio-alac.m4a", "audio/wav", 1, false)]
    public async Task StagesTheConfirmedTrackAtAnImmutableAttemptKeyAsync(string fixture, string mediaType, int channels, bool preservesBytes)
    {
        using var cancellation = new CancellationTokenSource();
        var original = await ReadFixtureAsync(fixture, cancellation.Token);
        var source = Source(original);
        using var client = new MediaAudioS3ClientStub(SourceKey, original);
        using var storage = new S3MediaAudioStorage(client, Decoder());

        var result = await storage.StageAsync(source, InputKey, cancellation.Token);

        Assert.Equal(channels, result.Channels);
        Assert.InRange(result.Duration, 0.55m, 0.70m);
        var (write, stagedBytes, _) = Assert.Single(client.Puts);
        var hash = SHA256.HashData(stagedBytes);
        var expectedKey = InputKey;
        Assert.Equal((Bucket, expectedKey, "*", mediaType), (write.BucketName, write.Key, write.IfNoneMatch, write.ContentType));
        Assert.Equal(Convert.ToBase64String(hash), write.ChecksumSHA256);
        Assert.Equal(source.VersionId, write.Metadata["source-version"]);
        Assert.Equal(Convert.ToBase64String(source.Sha256), write.Metadata["source-sha256"]);
        Assert.Equal((Bucket, expectedKey, client.WrittenVersion, (long)stagedBytes.Length),
            (result.Object.Bucket, result.Object.Key, result.Object.VersionId, result.Object.Size));
        Assert.Equal(hash, result.Object.Sha256);
        Assert.Equal(preservesBytes, original.AsSpan().SequenceEqual(stagedBytes));

        var originalRead = Assert.Single(client.Reads, read => read.Request.Key == SourceKey).Request;
        Assert.Equal((Bucket, source.VersionId), (originalRead.BucketName, originalRead.VersionId));
        var verifiedRead = Assert.Single(client.Reads, read => read.Request.Key == expectedKey).Request;
        Assert.Equal((Bucket, client.WrittenVersion), (verifiedRead.BucketName, verifiedRead.VersionId));
    }

    [FfmpegTheory]
    [InlineData("same")]
    [InlineData("changed-source")]
    [InlineData("changed-bytes")]
    public async Task ConditionalWriteConflictOnlyReusesTheSameStagedBytesAsync(string conflict)
    {
        var original = await ReadFixtureAsync("audio-pcm.wav", CancellationToken.None);
        using var client = new MediaAudioS3ClientStub(SourceKey, original) { ConflictOnPut = true };
        if (conflict == "changed-source")
            client.ExistingSourceHash = Convert.ToBase64String(SHA256.HashData([1, 2, 3]));
        if (conflict == "changed-bytes")
        {
            client.ExistingBytes = [.. original];
            client.ExistingBytes[^1] ^= 0xff;
        }
        using var storage = new S3MediaAudioStorage(client, Decoder());

        if (conflict == "same")
            Assert.Equal(client.WrittenVersion, (await storage.StageAsync(Source(original), InputKey, CancellationToken.None)).Object.VersionId);
        else
            await Assert.ThrowsAsync<MediaOutputInvalidException>(() => storage.StageAsync(Source(original), InputKey, CancellationToken.None));

        Assert.Single(client.Puts);
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("size")]
    [InlineData("attempt")]
    public async Task OriginalScopeOrIntegrityFailuresNeverStageBytesAsync(string difference)
    {
        var original = await ReadFixtureAsync("audio-pcm.wav", CancellationToken.None);
        var source = Source(original);
        using var client = new MediaAudioS3ClientStub(SourceKey, original);
        using var storage = new S3MediaAudioStorage(client, new AudioTranscodingDecoder("/unused/ffprobe"));

        if (difference == "sha")
            source = source with { Sha256 = SHA256.HashData([1, 2, 3]) };
        if (difference == "size")
            source = source with { Size = source.Size - 1 };
        var key = difference == "attempt" ? $"submissions/transcode-input/702/{RequestKey:N}/input" : InputKey;

        if (difference == "attempt")
        {
            await Assert.ThrowsAsync<MediaOutputInvalidException>(() => storage.StageAsync(source, key, CancellationToken.None));
            Assert.Empty(client.Reads);
        }
        else
        {
            await Assert.ThrowsAsync<MediaSourceIntegrityException>(() => storage.StageAsync(source, key, CancellationToken.None));
        }

        Assert.Empty(client.Puts);
    }

    [FfmpegTheory]
    [InlineData("audio-aac.m4a", true)]
    [InlineData("audio-opus.ogg", false)]
    [InlineData("audio-alac.m4a", false)]
    [InlineData("audio-with-video.mp4", false)]
    [InlineData("audio-default-second.m4a", false)]
    public async Task AcceptsOnlyAacLcAudioOnlyPlaybackAtThePinnedVersionAsync(string fixture, bool valid)
    {
        using var cancellation = new CancellationTokenSource();
        var bytes = await ReadFixtureAsync(fixture, cancellation.Token);
        using var client = new MediaAudioS3ClientStub(OutputKey, bytes) { SourceVersion = "output-version" };
        using var storage = new S3MediaAudioStorage(client, Decoder());

        if (valid)
        {
            var result = await storage.VerifyAsync(Bucket, OutputKey, cancellation.Token);
            Assert.Equal(("audio/mp4", "m4a", "aac", 1, 48000),
                (result.MediaType, result.Container, result.AudioCodec, result.Channels, result.SampleRate));
            Assert.InRange(result.Duration, 0.59m, 0.65m);
            Assert.Equal((Bucket, OutputKey, "output-version", (long)bytes.Length),
                (result.Object.Bucket, result.Object.Key, result.Object.VersionId, result.Object.Size));
            Assert.Equal(SHA256.HashData(bytes), result.Object.Sha256);
        }
        else
        {
            await Assert.ThrowsAsync<MediaOutputInvalidException>(() => storage.VerifyAsync(Bucket, OutputKey, cancellation.Token));
        }

        var head = Assert.Single(client.Heads).Request;
        var read = Assert.Single(client.Reads).Request;
        Assert.Equal((Bucket, OutputKey), (head.BucketName, head.Key));
        Assert.Equal((Bucket, OutputKey, "output-version"), (read.BucketName, read.Key, read.VersionId));
        Assert.Empty(client.Puts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    public async Task MissingOrChangedVersionNeverReturnsAVerifiedOutputAsync(string difference)
    {
        var bytes = await ReadFixtureAsync("audio-aac.m4a", CancellationToken.None);
        using var client = new MediaAudioS3ClientStub(OutputKey, bytes)
        {
            HeadVersion = difference == "missing" ? "null" : "expected-version",
            ReadVersion = "unexpected-version"
        };
        using var storage = new S3MediaAudioStorage(client, new AudioTranscodingDecoder("/unused/ffprobe"));

        await Assert.ThrowsAsync<MediaVariantStorageException>(() => storage.VerifyAsync(Bucket, OutputKey, CancellationToken.None));

        Assert.Single(client.Heads);
        if (difference == "missing")
            Assert.Empty(client.Reads);
        else
            Assert.Equal("expected-version", Assert.Single(client.Reads).Request.VersionId);
    }

    private static AudioTranscodingDecoder Decoder() => new(FfmpegTheoryAttribute.FfprobePath!);

    private static MediaSourceEntity Source(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return MediaPreparationTestFixture.Source() with
        {
            Kind = "audio", ObjectKey = SourceKey, Size = bytes.Length, Sha256 = hash, OriginalSha256 = [.. hash]
        };
    }

    private static Task<byte[]> ReadFixtureAsync(string name, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Decoders", "Fixtures", name), cancellationToken);
}
