using Amazon.MediaConvert;
using Amazon.MediaConvert.Model;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Messaging;
using static ScrapBook.UnitTests.Services.MediaAudioTestFixture;

namespace ScrapBook.UnitTests.Messaging;

public sealed class MediaConvertProviderTests
{
    private static string InputKey => $"submissions/transcode-input/701/{RequestKey:N}/input";

    [Theory]
    [InlineData(1, 96000)]
    [InlineData(2, 128000)]
    public async Task ConfiguresAStablePrivateAudioOnlyAacJobAsync(int channels, int bitrate)
    {
        using var client = new MediaConvertClientStub
        {
            Create = (_, _) => Task.FromResult(new CreateJobResponse { Job = Job(JobStatus.SUBMITTED) })
        };
        var provider = new MediaConvertProvider(client);
        var request = Request() with { Profile = Profile with { Channels = channels, Bitrate = bitrate } };

        var first = await provider.CreateAsync(request, CancellationToken.None);
        var second = await provider.CreateAsync(request, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal((ProviderJobId, RequestKey, Settings.QueueArn, "submitted"), (first.Id, first.RequestKey, first.QueueArn, first.State));
        Assert.Equal(2, client.Creates.Count);
        Assert.All(client.Creates, create =>
        {
            Assert.Equal(RequestKey.ToString("N"), create.ClientRequestToken);
            Assert.Equal(KeyValuePair.Create("requestKey", create.ClientRequestToken), Assert.Single(create.UserMetadata));
            Assert.Equal(Settings.QueueArn, create.Queue);
            Assert.Equal(Settings.JobRoleArn, create.Role);
            var input = Assert.Single(create.Settings.Inputs);
            Assert.Equal($"s3://{Bucket}/{InputKey}", input.FileInput);
            var selector = Assert.Single(input.AudioSelectors);
            Assert.Equal("Primary", selector.Key);
            Assert.Equal(AudioSelectorType.TRACK, selector.Value.SelectorType);
            Assert.Equal(1, Assert.Single(selector.Value.Tracks));
            var group = Assert.Single(create.Settings.OutputGroups);
            Assert.Equal($"s3://{Bucket}/{OutputKey[..^4]}", group.OutputGroupSettings.FileGroupSettings.Destination);
            var output = Assert.Single(group.Outputs);
            Assert.Null(output.VideoDescription);
            Assert.Equal("m4a", output.Extension);
            Assert.Equal(ContainerType.MP4, output.ContainerSettings.Container);
            Assert.Equal(Mp4MoovPlacement.PROGRESSIVE_DOWNLOAD, output.ContainerSettings.Mp4Settings.MoovPlacement);
            var audio = Assert.Single(output.AudioDescriptions);
            Assert.Equal(AudioCodec.AAC, audio.CodecSettings.Codec);
            Assert.Equal("Primary", audio.AudioSourceName);
            Assert.Equal(AacCodecProfile.LC, audio.CodecSettings.AacSettings.CodecProfile);
            Assert.Equal(AacSpecification.MPEG4, audio.CodecSettings.AacSettings.Specification);
            Assert.Equal(48000, audio.CodecSettings.AacSettings.SampleRate);
            Assert.Equal(bitrate, audio.CodecSettings.AacSettings.Bitrate);
            Assert.Equal(channels == 1 ? AacCodingMode.CODING_MODE_1_0 : AacCodingMode.CODING_MODE_2_0,
                audio.CodecSettings.AacSettings.CodingMode);
        });
    }

    [Theory]
    [InlineData(0, 0, 0, 1920, 1080, 30, 1, "DEGREE_0")]
    [InlineData(1, 96000, 90, 720, 1280, 24000, 1001, "DEGREES_270")]
    [InlineData(2, 128000, 270, 720, 1280, 30000, 1001, "DEGREES_90")]
    [InlineData(0, 0, 180, 1080, 1080, 25, 1, "DEGREES_180")]
    public async Task ConfiguresPrivateProgressiveVideoWithinTheEffectiveProfileAsync(int channels, int bitrate, int rotation,
        int width, int height, int numerator, int denominator, string expectedRotation)
    {
        using var client = new MediaConvertClientStub
        {
            Create = (_, _) => Task.FromResult(new CreateJobResponse { Job = Job(JobStatus.SUBMITTED) })
        };
        var provider = new MediaConvertProvider(client);
        var profile = Profile with { Channels = channels, Bitrate = bitrate, SampleRate = channels == 0 ? 0 : 48000 };
        var outputKey = Path.ChangeExtension(OutputKey, ".mp4");
        var request = Request() with
        {
            Profile = profile,
            OutputKey = outputKey,
            Video = new(width, height, numerator, denominator, rotation)
        };

        var job = await provider.CreateAsync(request, CancellationToken.None);

        Assert.Equal(RequestKey, job.RequestKey);
        var create = Assert.Single(client.Creates);
        Assert.Equal(RequestKey.ToString("N"), create.ClientRequestToken);
        Assert.Equal(KeyValuePair.Create("requestKey", create.ClientRequestToken), Assert.Single(create.UserMetadata));
        var input = Assert.Single(create.Settings.Inputs);
        Assert.Equal($"s3://{Bucket}/{InputKey}", input.FileInput);
        Assert.Equal(expectedRotation, input.VideoSelector.Rotate.Value);
        var group = Assert.Single(create.Settings.OutputGroups);
        Assert.Equal($"s3://{Bucket}/{outputKey[..^4]}", group.OutputGroupSettings.FileGroupSettings.Destination);
        var output = Assert.Single(group.Outputs);
        Assert.Equal("mp4", output.Extension);
        Assert.Equal(ContainerType.MP4, output.ContainerSettings.Container);
        Assert.Equal(Mp4MoovPlacement.PROGRESSIVE_DOWNLOAD, output.ContainerSettings.Mp4Settings.MoovPlacement);
        var video = output.VideoDescription;
        Assert.Equal((width, height), (video.Width, video.Height));
        Assert.Equal(ScalingBehavior.FIT_NO_UPSCALE, video.ScalingBehavior);
        Assert.Equal(VideoCodec.H_264, video.CodecSettings.Codec);
        var h264 = video.CodecSettings.H264Settings;
        Assert.Equal(H264RateControlMode.QVBR, h264.RateControlMode);
        Assert.Equal(7, h264.QvbrSettings.QvbrQualityLevel);
        Assert.Equal(5_000_000, h264.MaxBitrate);
        Assert.Equal(H264QualityTuningLevel.SINGLE_PASS_HQ, h264.QualityTuningLevel);
        Assert.Equal(H264FramerateControl.SPECIFIED, h264.FramerateControl);
        Assert.Equal(H264FramerateConversionAlgorithm.DUPLICATE_DROP, h264.FramerateConversionAlgorithm);
        Assert.Equal((numerator, denominator), (h264.FramerateNumerator, h264.FramerateDenominator));
        Assert.Equal(H264ParControl.SPECIFIED, h264.ParControl);
        Assert.Equal((1, 1), (h264.ParNumerator, h264.ParDenominator));
        if (channels == 0)
        {
            Assert.Null(input.AudioSelectors);
            Assert.Null(output.AudioDescriptions);
            return;
        }

        var selector = Assert.Single(input.AudioSelectors);
        Assert.Equal(AudioSelectorType.TRACK, selector.Value.SelectorType);
        Assert.Equal(1, Assert.Single(selector.Value.Tracks));
        var audio = Assert.Single(output.AudioDescriptions);
        Assert.Equal(selector.Key, audio.AudioSourceName);
        Assert.Equal(AudioCodec.AAC, audio.CodecSettings.Codec);
        var aac = audio.CodecSettings.AacSettings;
        Assert.Equal(AacCodecProfile.LC, aac.CodecProfile);
        Assert.Equal(AacSpecification.MPEG4, aac.Specification);
        Assert.Equal(48000, aac.SampleRate);
        Assert.Equal(bitrate, aac.Bitrate);
        Assert.Equal(channels == 1 ? AacCodingMode.CODING_MODE_1_0 : AacCodingMode.CODING_MODE_2_0, aac.CodingMode);
    }

    [Theory]
    [InlineData(641, 480, 30, 1, 0, 2, "mp4")]
    [InlineData(640, 0, 30, 1, 0, 2, "mp4")]
    [InlineData(640, 480, 0, 1, 0, 2, "mp4")]
    [InlineData(640, 480, 30, 0, 0, 2, "mp4")]
    [InlineData(640, 480, 30, 1, 45, 2, "mp4")]
    [InlineData(640, 480, 30, 1, 0, 3, "mp4")]
    [InlineData(640, 480, 30, 1, 0, 2, "m4a")]
    public async Task InvalidVideoJobSettingsNeverReachTheProviderAsync(int width, int height, int numerator,
        int denominator, int rotation, int channels, string extension)
    {
        using var client = new MediaConvertClientStub();
        var request = Request() with
        {
            Profile = Profile with { Channels = channels },
            OutputKey = Path.ChangeExtension(OutputKey, extension),
            Video = new(width, height, numerator, denominator, rotation)
        };

        var error = await Assert.ThrowsAsync<MediaJobRejectedException>(() =>
            new MediaConvertProvider(client).CreateAsync(request, CancellationToken.None));

        Assert.False(error.Retryable);
        Assert.Empty(client.Creates);
    }

    [Theory]
    [InlineData("bad-request", false)]
    [InlineData("forbidden", false)]
    [InlineData("throttled", true)]
    public async Task ClassifiesExplicitRejectionsSeparatelyFromUnknownSubmissionsAsync(string reason, bool retryable)
    {
        var rejection = reason switch
        {
            "bad-request" => (Exception)new BadRequestException("invalid"),
            "forbidden" => new ForbiddenException("denied"),
            _ => new TooManyRequestsException("throttled")
        };
        using var client = new MediaConvertClientStub { Create = (_, _) => throw rejection };

        var error = await Assert.ThrowsAsync<MediaJobRejectedException>(() =>
            new MediaConvertProvider(client).CreateAsync(Request(), CancellationToken.None));

        Assert.Equal(retryable, error.Retryable);
    }

    [Fact]
    public async Task SubmissionTimeoutIsIndeterminateAndTheAdapterDoesNotRetryAsync()
    {
        using var client = new MediaConvertClientStub { Create = (_, _) => throw new TimeoutException() };

        await Assert.ThrowsAsync<MediaProviderUnavailableException>(() =>
            new MediaConvertProvider(client).CreateAsync(Request(), CancellationToken.None));

        Assert.Single(client.Creates);
    }

    [Fact]
    public async Task PropagatesCallerCancellationToTheSdkAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new MediaConvertClientStub
        {
            Create = (_, token) =>
            {
                cancellation.Cancel();
                Assert.True(token.IsCancellationRequested);
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new CreateJobResponse());
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MediaConvertProvider(client).CreateAsync(Request(), cancellation.Token));

        Assert.Single(client.Creates);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("token")]
    [InlineData("role")]
    [InlineData("arn")]
    public async Task RejectsAConflictingJobIdentityInCreateResponseAsync(string difference)
    {
        var job = Job(JobStatus.SUBMITTED);
        if (difference == "queue") job.Queue += "-other";
        if (difference == "token") job.UserMetadata["requestKey"] = Guid.NewGuid().ToString("N");
        if (difference == "role") job.Role += "-other";
        if (difference == "arn") job.Arn = $"arn:aws:mediaconvert:us-east-1:111111111111:jobs/{ProviderJobId}";
        using var client = new MediaConvertClientStub
        {
            Create = (_, _) => Task.FromResult(new CreateJobResponse { Job = job })
        };

        await Assert.ThrowsAsync<MediaProviderUnavailableException>(() =>
            new MediaConvertProvider(client).CreateAsync(Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData("m4a")]
    [InlineData("mp4")]
    public async Task ReadsTheExpectedPlaybackLocationFromTheCompletedJobAsync(string extension)
    {
        var outputKey = Path.ChangeExtension(OutputKey, extension);
        var job = Job(JobStatus.COMPLETE);
        job.ClientRequestToken = RequestKey.ToString("N");
        job.Settings = new()
        {
            Inputs = [new() { FileInput = $"s3://{Bucket}/{InputKey}" }],
            OutputGroups =
            [
                new()
                {
                    OutputGroupSettings = new() { FileGroupSettings = new() { Destination = $"s3://{Bucket}/{outputKey[..^4]}" } },
                    Outputs = [new() { Extension = extension }]
                }
            ]
        };
        using var client = new MediaConvertClientStub { Get = (_, _) => Task.FromResult(new GetJobResponse { Job = job }) };

        var result = await new MediaConvertProvider(client).GetAsync(ProviderJobId, CancellationToken.None);

        Assert.Equal(ProviderJob(RequestKey, "complete", outputKey) with
        {
            JobRoleArn = Settings.JobRoleArn,
            ClientRequestToken = RequestKey.ToString("N"),
            InputUri = $"s3://{Bucket}/{InputKey}",
            OutputDestination = $"s3://{Bucket}/{outputKey[..^4]}"
        }, result);
        Assert.Equal(ProviderJobId, Assert.Single(client.Gets).Id);
    }

    [Fact]
    public async Task MissingKnownJobDoesNotBecomeAConclusiveFailureAsync()
    {
        using var client = new MediaConvertClientStub { Get = (_, _) => throw new NotFoundException("missing") };

        Assert.Null(await new MediaConvertProvider(client).GetAsync(ProviderJobId, CancellationToken.None));
    }

    [Fact]
    public async Task FindsOnlyTheMatchingJobWithinTheOriginalSubmissionIntervalAsync()
    {
        var unrelated = Job(JobStatus.SUBMITTED);
        unrelated.UserMetadata["requestKey"] = Guid.NewGuid().ToString("N");
        var recent = Job(JobStatus.SUBMITTED);
        recent.CreatedAt = Now.AddMinutes(1).UtcDateTime;
        var old = Job(JobStatus.SUBMITTED);
        old.CreatedAt = Now.AddMinutes(-1).UtcDateTime;
        using var client = new MediaConvertClientStub
        {
            List = (query, _) => Task.FromResult(query.NextToken is null
                ? new ListJobsResponse { Jobs = [recent, unrelated], NextToken = "page-two" }
                : new ListJobsResponse { Jobs = [Job(JobStatus.PROGRESSING), old] })
        };

        var found = await new MediaConvertProvider(client).FindAsync(Request(), Now, CancellationToken.None);

        Assert.Equal(ProviderJob(RequestKey, "progressing") with { JobRoleArn = Settings.JobRoleArn }, found);
        Assert.Equal([("private-audio", "DESCENDING", (int?)20, (string?)null), ("private-audio", "DESCENDING", (int?)20, "page-two")],
            client.Queries);
    }

    [Fact]
    public async Task ConflictingJobsWithTheSameRequestKeyStayIndeterminateAsync()
    {
        var duplicate = Job(JobStatus.SUBMITTED);
        duplicate.Id += "-another";
        duplicate.Arn += "-another";
        using var client = new MediaConvertClientStub
        {
            List = (_, _) => Task.FromResult(new ListJobsResponse { Jobs = [Job(JobStatus.SUBMITTED), duplicate] })
        };

        await Assert.ThrowsAsync<MediaProviderUnavailableException>(() =>
            new MediaConvertProvider(client).FindAsync(Request(), Now, CancellationToken.None));
    }

    private static MediaAudioRequestModel Request() => new(RequestKey, Bucket, InputKey, OutputKey, Profile);

    private static Job Job(JobStatus status) => new()
    {
        Id = ProviderJobId,
        Arn = $"arn:aws:mediaconvert:us-east-1:123456789012:jobs/{ProviderJobId}",
        Role = Settings.JobRoleArn,
        Queue = Settings.QueueArn,
        Status = status,
        CreatedAt = Now.UtcDateTime,
        UserMetadata = new Dictionary<string, string> { ["requestKey"] = RequestKey.ToString("N") }
    };
}
