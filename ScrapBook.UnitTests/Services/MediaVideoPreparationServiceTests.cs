using System.Globalization;
using System.Text.Json;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using static ScrapBook.UnitTests.Services.MediaAudioTestFixture;
using static ScrapBook.UnitTests.Services.MediaVideoTestFixture;

namespace ScrapBook.UnitTests.Services;

public sealed class MediaVideoPreparationServiceTests
{
    [Theory]
    [InlineData(3840, 2160, 1, 1, 0, 1920, 1080, 0)]
    [InlineData(3840, 2160, 1, 1, 90, 1080, 1920, 90)]
    [InlineData(3840, 2160, 1, 1, 270, 1080, 1920, 270)]
    [InlineData(1080, 1920, 1, 1, 180, 1080, 1920, 180)]
    [InlineData(2160, 2160, 1, 1, 0, 1080, 1080, 0)]
    [InlineData(2560, 1440, 3, 4, 0, 1440, 1080, 0)]
    [InlineData(2560, 1440, 3, 4, 90, 1080, 1440, 90)]
    [InlineData(641, 481, 1, 1, 0, 640, 480, 0)]
    [InlineData(320, 240, 4, 3, 0, 320, 180, 0)]
    [InlineData(320, 240, 4, 3, -90, 180, 320, 270)]
    [InlineData(320, 240, 4, 3, -180, 320, 180, 180)]
    public async Task SelectsEvenDisplayDimensionsWithinTheSourceAndOrientationBoundsAsync(int width, int height,
        int sarNumerator, int sarDenominator, int rotation, int expectedWidth, int expectedHeight, int expectedRotation)
    {
        var source = SourceProperties with
        {
            Width = width,
            Height = height,
            PixelAspectNumerator = sarNumerator,
            PixelAspectDenominator = sarDenominator,
            Rotation = rotation
        };
        var repository = Pending();
        var storage = Storage(source);
        var provider = new MediaTranscodingProviderStub();

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        var video = Assert.Single(provider.Submissions).Request.Video;
        Assert.Equal(new MediaVideoProfileModel(expectedWidth, expectedHeight, 30, 1, expectedRotation), video);
    }

    [Theory]
    [InlineData(24000, 1001, false, 24000, 1001)]
    [InlineData(30000, 1001, false, 30000, 1001)]
    [InlineData(60, 2, false, 30, 1)]
    [InlineData(30001, 1000, false, 30, 1)]
    [InlineData(60000, 1001, false, 30, 1)]
    [InlineData(15, 1, true, 15, 1)]
    [InlineData(60, 1, true, 30, 1)]
    public async Task KeepsConservativeSourceCadenceAndCapsAtThirtyFramesPerSecondAsync(int numerator, int denominator,
        bool variable, int expectedNumerator, int expectedDenominator)
    {
        var source = SourceProperties with
        {
            FrameRateNumerator = numerator,
            FrameRateDenominator = denominator,
            VariableFrameRate = variable
        };
        var provider = new MediaTranscodingProviderStub();

        Assert.True(await Service(Pending(), Storage(source), provider).PrepareNextAsync(CancellationToken.None));

        var video = Assert.Single(provider.Submissions).Request.Video;
        Assert.NotNull(video);
        Assert.Equal((expectedNumerator, expectedDenominator), (video.FrameRateNumerator, video.FrameRateDenominator));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 48000, 96000)]
    [InlineData(2, 48000, 128000)]
    public async Task PersistsTheVersionedVideoInputAndAudioProfileBeforeSubmissionAsync(int channels, int rate, int bitrate)
    {
        using var cancellation = new CancellationTokenSource();
        var repository = Pending();
        var source = SourceProperties with
        {
            AudioCodec = channels == 0 ? null : "aac",
            AudioChannels = channels,
            AudioSampleRate = channels == 0 ? 0 : 44100,
            AudioProfile = channels == 0 ? null : "LC"
        };
        var storage = new MediaVideoStorageStub
        {
            Stage = (_, key, _) =>
            {
                Assert.True(Assert.Single(repository.Starts).Transaction.Committed);
                return Task.FromResult(MediaVideoTestFixture.Input(key, source));
            }
        };
        var provider = new MediaTranscodingProviderStub
        {
            Create = (request, token) =>
            {
                Assert.True(token.CanBeCanceled);
                var input = Assert.Single(repository.Inputs);
                Assert.True(input.Transaction.Committed);
                Assert.True(Assert.Single(repository.Submissions).Transaction.Committed);
                var plan = JsonSerializer.Deserialize<MediaAudioPlanModel>(input.Plan);
                Assert.NotNull(plan?.VideoInput);
                var staged = plan.VideoInput.Object;
                Assert.Equal((input.Object.Bucket, input.Object.Key, input.Object.VersionId, input.Object.Size),
                    (staged.Bucket, staged.Key, staged.VersionId, staged.Size));
                Assert.Equal(input.Object.Sha256, staged.Sha256);
                Assert.Equal(source, plan.VideoInput.Properties);
                Assert.Equal(request.Video, plan.VideoProfile);
                Assert.Equal((channels, rate, bitrate), (request.Profile.Channels, request.Profile.SampleRate, request.Profile.Bitrate));
                return Task.FromResult(ProviderJob(request.RequestKey));
            }
        };

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(cancellation.Token));

        var staged = Assert.Single(storage.Staged);
        var request = Assert.Single(provider.Submissions).Request;
        Assert.Equal(repository.Source, staged.Source);
        Assert.Equal("confirmed-version", staged.Source.VersionId);
        Assert.True(staged.Token.CanBeCanceled);
        Assert.Equal($"submissions/transcode-input/701/{request.RequestKey:N}/input", staged.Key);
        Assert.Equal((Bucket, staged.Key, $"submissions/transcode-output/701/{request.RequestKey:N}/playback.mp4"),
            (request.Bucket, request.InputKey, request.OutputKey));
        Assert.Equal(request.RequestKey, repository.Attempt!.RequestKey);
        Assert.Empty(repository.Promotions);
    }

    [Fact]
    public async Task LeavesPendingVideoUntouchedWhenVideoTranscodingIsDisabledAsync()
    {
        var repository = Pending();
        var storage = new MediaVideoStorageStub();
        var provider = new MediaTranscodingProviderStub();
        var service = new MediaAudioPreparationService(repository, new MediaAudioStorageStub(), provider, Settings,
            new EventTestTimeProvider { Now = Now }, storage);

        Assert.False(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Equal("pending", repository.Job!.State);
        Assert.Empty(repository.Starts);
        Assert.Empty(repository.Failures);
        Assert.Empty(storage.Staged);
        Assert.Empty(provider.Submissions);
    }

    [Theory]
    [InlineData("missing-version")]
    [InlineData("unconfirmed-file")]
    [InlineData("oversized")]
    public async Task RejectsAnOriginalOutsideTheConfirmedVideoScopeBeforeStageAsync(string difference)
    {
        var repository = Pending();
        var source = repository.Source!;
        repository.Source = difference switch
        {
            "missing-version" => source with { VersionId = "null" },
            "unconfirmed-file" => source with { FileId = source.FileId + 1 },
            _ => source with { Size = 500_000_001 }
        };
        var storage = new MediaVideoStorageStub();
        var provider = new MediaTranscodingProviderStub();

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.Empty(storage.Staged);
        Assert.Empty(provider.Submissions);
        Assert.Empty(repository.Starts);
        Assert.Equal("MEDIA_SOURCE_INTEGRITY", Assert.Single(repository.States).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ReconcilesTheUnknownVideoSubmissionAndPromotesThePinnedVariantOnceAsync(int channels)
    {
        var repository = Pending();
        var storage = Storage(SourceProperties with
        {
            AudioCodec = channels == 0 ? null : "aac",
            AudioChannels = channels,
            AudioSampleRate = channels == 0 ? 0 : 44100,
            AudioProfile = channels == 0 ? null : "LC"
        });
        storage.Verify = (_, key, _) => Task.FromResult(MediaVideoTestFixture.Variant(key, channels));
        var provider = new MediaTranscodingProviderStub { Create = (_, _) => throw new MediaProviderUnavailableException() };
        var service = Service(repository, storage, provider);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        var submission = Assert.Single(provider.Submissions).Request;
        Assert.Equal("unknown", repository.Attempt!.State);
        Assert.Equal("reconciling", repository.Job!.State);
        provider.Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(Completed(submission));
        var callback = Callback(submission.RequestKey);
        Assert.True(await service.WakeAsync(callback, CancellationToken.None));
        Assert.False(await service.WakeAsync(callback, CancellationToken.None));
        Assert.Empty(storage.Verified);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        var verified = Assert.Single(storage.Verified);
        Assert.Equal((Bucket, submission.OutputKey), (verified.Bucket, verified.Key));
        var promotion = Assert.Single(repository.Promotions);
        Assert.Equal((submission.OutputKey, "output-version", channels),
            (promotion.Variant.Object.Key, promotion.Variant.Object.VersionId, promotion.Variant.Channels));
        Assert.Equal(repository.Attempt.Id, promotion.Work.Attempt.Id);
        Assert.Equal(promotion.Work.Job.ActiveAttemptId, promotion.Work.Attempt.Id);
        Assert.Equal(promotion.Transaction, Assert.Single(repository.States, state => state.FileState == "available").Transaction);
        Assert.True(promotion.Transaction.Committed);
        Assert.False(await service.WakeAsync(callback, CancellationToken.None));
        Assert.False(await service.PrepareNextAsync(CancellationToken.None));
        Assert.Single(provider.Submissions);
        Assert.Single(repository.Promotions);
        Assert.Empty(repository.Failures);
    }

    [Fact]
    public async Task PromotesAnOrientedPlaybackWithinEncoderRoundingAndEquivalentFrameRateAsync()
    {
        var source = SourceProperties with
        {
            Width = 3840,
            Height = 2160,
            Rotation = 90,
            FrameRateNumerator = 25,
            FrameRateDenominator = 1
        };
        var repository = Pending();
        var storage = Storage(source);
        storage.Verify = (_, key, _) =>
        {
            var output = MediaVideoTestFixture.Variant(key);
            var oriented = output.Video! with { Width = 1080, Height = 1918, FrameRateNumerator = 50, FrameRateDenominator = 2 };
            return Task.FromResult(output with { Video = oriented });
        };
        var provider = new MediaTranscodingProviderStub { Create = (request, _) => Task.FromResult(Completed(request)) };
        var service = Service(repository, storage, provider);
        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        var request = Assert.Single(provider.Submissions).Request;
        Assert.Equal(new MediaVideoProfileModel(1080, 1920, 25, 1, 90), request.Video);
        provider.Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(Completed(request));

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Single(repository.Promotions);
        Assert.Equal("available", Assert.Single(repository.States).FileState);
    }

    [Fact]
    public async Task IgnoresThePriorVideoCallbackWhileTheReplacementAttemptRemainsActiveAsync()
    {
        var repository = Pending();
        var storage = new MediaVideoStorageStub();
        var provider = new MediaTranscodingProviderStub();
        var clock = new EventTestTimeProvider { Now = Now };
        var service = Service(repository, storage, provider, clock);
        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        var first = Assert.Single(provider.Submissions).Request;
        clock.Now = repository.NextAt!.Value;
        provider.Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(ProviderJob(first.RequestKey, "error"));
        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        clock.Now = repository.NextAt!.Value;
        const string secondJobId = ProviderJobId + "-retry";
        provider.Create = (request, _) => Task.FromResult(ProviderJob(request.RequestKey) with { Id = secondJobId });
        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        var second = provider.Submissions[1].Request;
        Assert.NotEqual(first.RequestKey, second.RequestKey);
        Assert.NotEqual(first.InputKey, second.InputKey);
        Assert.NotEqual(first.OutputKey, second.OutputKey);
        var readCount = provider.Reads.Count;

        Assert.False(await service.WakeAsync(Callback(first.RequestKey), CancellationToken.None));

        Assert.Equal(readCount, provider.Reads.Count);
        Assert.Empty(repository.Wakes);
        Assert.Empty(storage.Verified);
        Assert.Empty(repository.Promotions);
        provider.Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(Completed(second) with { Id = secondJobId });
        Assert.True(await service.WakeAsync(Callback(second.RequestKey) with { JobId = secondJobId }, CancellationToken.None));
        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        Assert.Equal(second.OutputKey, Assert.Single(repository.Promotions).Variant.Object.Key);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("input")]
    [InlineData("request")]
    public async Task AForeignProviderCompletionCannotCauseAVideoReadAsync(string difference)
    {
        var repository = Pending();
        var storage = new MediaVideoStorageStub();
        var provider = new MediaTranscodingProviderStub { Create = (request, _) => Task.FromResult(Completed(request)) };
        var service = Service(repository, storage, provider);
        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        var request = Assert.Single(provider.Submissions).Request;
        var completed = Completed(request);
        provider.Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(difference switch
        {
            "output" => completed with { OutputUri = $"s3://{Bucket}/submissions/transcode-output/702/{request.RequestKey:N}/playback.mp4" },
            "input" => completed with { InputUri = $"s3://{Bucket}/submissions/transcode-input/702/{request.RequestKey:N}/input" },
            _ => completed with { RequestKey = RequestKey }
        });

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Empty(storage.Verified);
        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.Failures);
        Assert.Equal("MEDIA_PROVIDER_RESULT_UNVERIFIED", repository.Releases[^1].Code);
    }

    [Theory]
    [InlineData("container")]
    [InlineData("codec")]
    [InlineData("version")]
    [InlineData("size")]
    [InlineData("sha256")]
    [InlineData("location")]
    [InlineData("wrong-aspect")]
    [InlineData("upscaled")]
    [InlineData("still-rotated")]
    [InlineData("non-square-pixels")]
    [InlineData("faster")]
    [InlineData("variable")]
    [InlineData("duration")]
    [InlineData("aac-profile")]
    [InlineData("missing-audio")]
    [InlineData("inserted-audio")]
    public async Task RejectsVideoArtifactsOutsideTheAuthorizedProfileAsync(string difference)
    {
        var repository = Pending();
        var source = difference == "inserted-audio"
            ? SourceProperties with { AudioCodec = null, AudioChannels = 0, AudioSampleRate = 0, AudioProfile = null }
            : SourceProperties;
        var storage = Storage(source);
        storage.Verify = (_, key, _) => Task.FromResult(InvalidVariant(key, difference));
        var provider = new MediaTranscodingProviderStub { Create = (request, _) => Task.FromResult(Completed(request)) };
        var service = Service(repository, storage, provider);
        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        provider.Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(Completed(Assert.Single(provider.Submissions).Request));

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Single(storage.Verified);
        Assert.Empty(repository.Promotions);
        var transition = Assert.Single(repository.States);
        Assert.Equal(("failed", "MEDIA_VARIANT_INVALID"), (transition.FileState, transition.Code));
        Assert.Same(transition.Transaction, Assert.Single(repository.Failures).Transaction);
    }

    [Fact]
    public async Task DoesNotPublishAVerifiedVideoAfterItsLeaseIsLostAsync()
    {
        var repository = Pending();
        var storage = new MediaVideoStorageStub
        {
            Verify = (_, key, _) =>
            {
                repository.RetainsLease = false;
                return Task.FromResult(MediaVideoTestFixture.Variant(key));
            }
        };
        var provider = new MediaTranscodingProviderStub { Create = (request, _) => Task.FromResult(Completed(request)) };
        var service = Service(repository, storage, provider);
        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        provider.Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(Completed(Assert.Single(provider.Submissions).Request));

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Single(storage.Verified);
        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.States);
        Assert.Empty(repository.Failures);
    }

    [Fact]
    public async Task ADefiniteVideoFailurePreservesConfirmationAndRecordsOneSanitizedF13ResultAsync()
    {
        var repository = Pending();
        var source = repository.Source!;
        var provider = new MediaTranscodingProviderStub { Create = (_, _) => throw new MediaJobRejectedException(false) };
        var service = Service(repository, new(), provider);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        Assert.False(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Equal(source, repository.Source);
        Assert.Equal("confirmed", repository.Source!.RecordState);
        Assert.Equal(source.RecordConfirmedAt, repository.Source.RecordConfirmedAt);
        Assert.Equal(source.VersionId, repository.Source.VersionId);
        var (failure, transaction) = Assert.Single(repository.Failures);
        var transition = Assert.Single(repository.States);
        Assert.Equal(("failed", "failed", "failed"), (transition.JobState, transition.FileState, transition.VariantState));
        Assert.Same(transition.Transaction, transaction);
        Assert.True(transaction.Committed);
        var confirmation = MediaPreparationTestFixture.Confirmation;
        Assert.Equal("record.media-preparation.failed.v1", failure.Type);
        Assert.Equal((confirmation.EventId, confirmation.CorrelationId, confirmation.MessageId, Now),
            (failure.EventId, failure.CorrelationId, failure.CausationId, failure.OccurredAt));
        using var payload = JsonDocument.Parse(failure.Payload);
        Assert.Equal(confirmation.RecordId.ToString(CultureInfo.InvariantCulture), payload.RootElement.GetProperty("recordId").GetString());
        Assert.Equal(confirmation.FileId.ToString(CultureInfo.InvariantCulture), payload.RootElement.GetProperty("fileId").GetString());
        Assert.Equal("transcode", payload.RootElement.GetProperty("stage").GetString());
        Assert.Equal("MEDIA_PROVIDER_REJECTED", payload.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(source.Bucket, failure.Payload);
        Assert.DoesNotContain(source.ObjectKey, failure.Payload);
        Assert.DoesNotContain(source.VersionId, failure.Payload);
        Assert.Empty(repository.Promotions);
    }

    private static MediaAudioVariantModel InvalidVariant(string key, string difference)
    {
        var variant = MediaVideoTestFixture.Variant(key);
        var video = variant.Video!;
        return difference switch
        {
            "container" => variant with { Container = "mov", Video = video with { Container = "mov" } },
            "codec" => variant with { Video = video with { VideoCodec = "vp9" } },
            "version" => variant with { Object = variant.Object with { VersionId = "null" } },
            "size" => variant with { Object = variant.Object with { Size = 0 } },
            "sha256" => variant with { Object = variant.Object with { Sha256 = [1, 2, 3] } },
            "location" => variant with { Video = video with { HasLocationMetadata = true } },
            "wrong-aspect" => variant with { Video = video with { Width = 1000 } },
            "upscaled" => variant with { Video = video with { Width = 1920, Height = 1080 } },
            "still-rotated" => variant with { Video = video with { Rotation = 90 } },
            "non-square-pixels" => variant with { Video = video with { PixelAspectNumerator = 4, PixelAspectDenominator = 3 } },
            "faster" => variant with { Video = video with { FrameRateNumerator = 31 } },
            "variable" => variant with { Video = video with { VariableFrameRate = true } },
            "duration" => variant with { Duration = 8m, Video = video with { Duration = 8m } },
            "aac-profile" => variant with { Video = video with { AudioProfile = "HE-AAC" } },
            "missing-audio" => MediaVideoTestFixture.Variant(key, 0),
            "inserted-audio" => variant,
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
    }

    private static MediaVideoStorageStub Storage(MediaVideoPropertiesModel source) => new()
    {
        Stage = (_, key, _) => Task.FromResult(MediaVideoTestFixture.Input(key, source))
    };

    private static MediaAudioPreparationService Service(MediaAudioRepositoryStub repository, MediaVideoStorageStub storage,
        MediaTranscodingProviderStub provider, TimeProvider? clock = null) =>
        new(repository, new MediaAudioStorageStub(), provider, Settings with { VideoEnabled = true },
            clock ?? new EventTestTimeProvider { Now = Now }, storage);
}
