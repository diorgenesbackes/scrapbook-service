using System.Text.Json;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using static ScrapBook.UnitTests.Services.MediaAudioTestFixture;

namespace ScrapBook.UnitTests.Services;

public sealed class MediaAudioPreparationServiceTests
{
    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    public async Task GivesNewTranscodesPriorityAndHonorsHistoricalCapacityAsync(bool newWork, bool allowHistorical,
        bool preferHistorical, bool takesHistory)
    {
        using var cancellation = new CancellationTokenSource();
        var ordinary = Job();
        var historical = ordinary with { Id = ordinary.Id + 1 };
        var repository = new MediaAudioRepositoryStub { Job = newWork ? ordinary : null, HistoricalJob = historical };
        var service = Service(repository, new(), new());

        Assert.Equal(newWork || takesHistory, await service.PrepareNextAsync(allowHistorical, preferHistorical, cancellation.Token));

        var historicalFirst = allowHistorical && preferHistorical;
        Assert.Equal((historicalFirst, cancellation.Token), repository.EligibilityChecks[0]);
        if (takesHistory)
        {
            if (historicalFirst) Assert.Single(repository.EligibilityChecks);
            else Assert.Equal((true, cancellation.Token), repository.EligibilityChecks[1]);
            Assert.Equal(historical.Id, repository.Job!.Id);
        }
        else
        {
            Assert.Single(repository.EligibilityChecks);
            if (newWork) Assert.Equal(ordinary.Id, repository.Job!.Id);
            else Assert.Null(repository.Job);
        }
    }

    [Theory]
    [InlineData(1, 96000)]
    [InlineData(2, 128000)]
    public async Task PersistsKeysVerifiedInputProfileAndClockBeforeTheProviderCallAsync(int channels, int bitrate)
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new MediaAudioRepositoryStub();
        var storage = new MediaAudioStorageStub
        {
            Stage = (_, key, _) =>
            {
                Assert.True(Assert.Single(repository.Starts).Transaction.Committed);
                return Task.FromResult(Input(key, channels));
            }
        };
        var provider = new MediaTranscodingProviderStub
        {
            Create = (request, token) =>
            {
                var staged = Assert.Single(repository.Inputs);
                var submission = Assert.Single(repository.Submissions);
                Assert.True(staged.Transaction.Committed);
                Assert.True(submission.Transaction.Committed);
                Assert.Equal(Now, submission.At);
                var plan = JsonSerializer.Deserialize<MediaAudioPlanModel>(staged.Plan);
                Assert.NotNull(plan);
                Assert.Equal(Settings, plan.Settings);
                Assert.Equal(staged.Object.Key, request.InputKey);
                Assert.Equal(staged.Object.Sha256, plan.Input!.Object.Sha256);
                Assert.Equal("staged-version", plan.Input.Object.VersionId);
                Assert.Equal(plan.Profile, request.Profile);
                Assert.Equal(Profile with { Channels = channels, Bitrate = bitrate }, request.Profile);
                Assert.Equal(repository.Attempt!.RequestKey, request.RequestKey);
                Assert.True(token.CanBeCanceled);
                return Task.FromResult(ProviderJob(request.RequestKey));
            }
        };
        var service = Service(repository, storage, provider);

        Assert.True(await service.PrepareNextAsync(cancellation.Token));

        var stage = Assert.Single(storage.Staged);
        var request = Assert.Single(provider.Submissions).Request;
        Assert.Equal(repository.Source, stage.Source);
        Assert.Equal("confirmed-version", stage.Source.VersionId);
        Assert.True(stage.Token.CanBeCanceled);
        Assert.Equal(Bucket, request.Bucket);
        Assert.Equal($"submissions/transcode-input/701/{request.RequestKey:N}/input", stage.Key);
        Assert.Equal($"submissions/transcode-output/701/{request.RequestKey:N}/playback.m4a", request.OutputKey);
        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.Failures);
        Assert.Equal(ProviderJobId, repository.Attempt!.ProviderJobId);
    }

    [Fact]
    public async Task LostCreateResponseKeepsOneAttemptAndEarlyCallbackVerifiesOutputOnceAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new MediaAudioRepositoryStub();
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub
        {
            Create = (_, _) => throw new MediaProviderUnavailableException(),
            Get = (id, _) =>
            {
                Assert.Equal(ProviderJobId, id);
                var request = Assert.Single(repository.Starts);
                return Task.FromResult<MediaProviderJobModel?>(ProviderJob(repository.Attempt!.RequestKey, "complete", request.OutputKey));
            }
        };
        var service = Service(repository, storage, provider);

        Assert.True(await service.PrepareNextAsync(cancellation.Token));

        var submitted = Assert.Single(provider.Submissions).Request;
        Assert.Equal("unknown", repository.Attempt!.State);
        Assert.Equal("reconciling", repository.Job!.State);
        Assert.Equal(repository.Attempt.Id, repository.Job.ActiveAttemptId);
        Assert.Empty(repository.Failures);
        Assert.Empty(storage.Verified);
        Assert.True(await service.WakeAsync(Callback(submitted.RequestKey), cancellation.Token));
        Assert.False(await service.WakeAsync(Callback(submitted.RequestKey), cancellation.Token));
        Assert.Single(repository.Wakes);
        Assert.Empty(storage.Verified);

        Assert.True(await service.PrepareNextAsync(cancellation.Token));

        var promotion = Assert.Single(repository.Promotions);
        var transition = Assert.Single(repository.States, state => state.FileState == "available");
        var verified = Assert.Single(storage.Verified);
        Assert.Equal((Bucket, submitted.OutputKey), (verified.Bucket, verified.Key));
        Assert.Equal("output-version", promotion.Variant.Object.VersionId);
        Assert.Equal(transition.Transaction, promotion.Transaction);
        Assert.True(promotion.Transaction.Committed);
        Assert.False(await service.WakeAsync(Callback(submitted.RequestKey), cancellation.Token));
        Assert.False(await service.PrepareNextAsync(cancellation.Token));
        Assert.Single(repository.Promotions);
        Assert.Single(provider.Submissions);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(35, true)]
    [InlineData(36, false)]
    [InlineData(40, false)]
    [InlineData(45, false)]
    [InlineData(90, false)]
    public async Task AnInconclusiveLookupAllowsOnlyTheSameRequestInsideCallBudgetAsync(int elapsedSeconds, bool mayResubmit)
    {
        var submittedAt = Now.AddSeconds(-elapsedSeconds);
        var repository = InFlight(submittedAt);
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub { Create = (_, _) => throw new MediaProviderUnavailableException() };
        var service = Service(repository, storage, provider);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        var search = Assert.Single(provider.Searches);
        Assert.Equal(submittedAt, search.SubmittedAt);
        Assert.Equal(RequestKey, search.Request.RequestKey);
        if (mayResubmit)
            Assert.Equal(search.Request, Assert.Single(provider.Submissions).Request);
        else
            Assert.Empty(provider.Submissions);

        Assert.Empty(storage.Staged);
        Assert.Empty(repository.Starts);
        Assert.Empty(repository.Submissions);
        Assert.Equal(submittedAt, repository.Attempt!.SubmittedAt);
        Assert.Equal("unknown", repository.Attempt.State);
        Assert.Equal("reconciling", repository.Job!.State);
        Assert.Empty(repository.Failures);
    }

    [Fact]
    public async Task ARejectedReplayDoesNotProveThePreviousRequestHadNoEffectAsync()
    {
        var repository = InFlight(Now.AddSeconds(-5));
        var provider = new MediaTranscodingProviderStub { Create = (_, _) => throw new MediaJobRejectedException(false) };

        Assert.True(await Service(repository, new(), provider).PrepareNextAsync(CancellationToken.None));

        Assert.Single(provider.Submissions);
        Assert.Equal("reconciling", repository.Job!.State);
        Assert.Equal("unknown", repository.Attempt!.State);
        Assert.Empty(repository.Failures);
    }

    [Fact]
    public async Task DoesNotSubmitWhenTheLeaseCanNoLongerCoverTheProviderCallAsync()
    {
        var repository = new MediaAudioRepositoryStub { RetainsLease = false };
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub();

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.NotEmpty(repository.LeaseChecks);
        Assert.Empty(provider.Submissions);
        Assert.Empty(repository.Failures);
    }

    [Fact]
    public async Task FindsAJobByTheOriginalTokenAfterTheSubmissionWindowAsync()
    {
        var repository = InFlight(Now.AddMinutes(-2));
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub
        {
            Find = (_, _, _) => Task.FromResult<MediaProviderJobModel?>(ProviderJob(RequestKey, "progressing"))
        };

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.Single(provider.Searches);
        Assert.Empty(provider.Submissions);
        Assert.Equal(ProviderJobId, repository.Attempt!.ProviderJobId);
        Assert.Empty(storage.Verified);
        Assert.Empty(repository.Promotions);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("region")]
    [InlineData("queue")]
    [InlineData("job")]
    [InlineData("request")]
    public async Task IgnoresCallbacksOutsideTheCurrentAttemptScopeAsync(string difference)
    {
        var repository = InFlight(Now.AddMinutes(-1), ProviderJobId);
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub();
        var message = difference switch
        {
            "account" => Callback() with { Account = "098765432109" },
            "region" => Callback() with { Region = "us-west-2" },
            "queue" => Callback() with { QueueArn = Settings.QueueArn + "-other" },
            "job" => Callback() with { JobId = ProviderJobId + "-stale" },
            _ => Callback(Guid.Parse("b245a7e1-e8a8-4c0a-9dbc-d1ffc123e54f"))
        };

        Assert.False(await Service(repository, storage, provider).WakeAsync(message, CancellationToken.None));

        Assert.Empty(repository.Wakes);
        Assert.Empty(repository.States);
        Assert.Empty(provider.Reads);
        Assert.Empty(storage.Verified);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("request")]
    [InlineData("key")]
    [InlineData("bucket")]
    [InlineData("job")]
    [InlineData("role")]
    [InlineData("client-token")]
    [InlineData("input-uri")]
    [InlineData("destination")]
    public async Task ProviderCompletionMustMatchTheLocalAttemptAndArtifactAsync(string difference)
    {
        var repository = InFlight(Now.AddMinutes(-1), ProviderJobId);
        var storage = new MediaAudioStorageStub();
        var valid = ProviderJob(RequestKey, "complete", OutputKey);
        var other = difference switch
        {
            "queue" => valid with { QueueArn = Settings.QueueArn + "-other" },
            "request" => valid with { RequestKey = Guid.Empty },
            "key" => valid with { OutputUri = $"s3://{Bucket}/submissions/transcode-output/702/{RequestKey:N}/playback.m4a" },
            "bucket" => valid with { OutputUri = $"s3://other-bucket/{OutputKey}" },
            "role" => valid with { JobRoleArn = Settings.JobRoleArn + "-other" },
            "client-token" => valid with { ClientRequestToken = Guid.Empty.ToString("N") },
            "input-uri" => valid with { InputUri = $"s3://{Bucket}/submissions/transcode-input/702/{RequestKey:N}/input" },
            "destination" => valid with { OutputDestination = $"s3://other-bucket/{OutputKey[..^4]}" },
            _ => valid with { Id = ProviderJobId + "-other" }
        };
        var provider = new MediaTranscodingProviderStub { Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(other) };

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.Empty(storage.Verified);
        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.Failures);
        Assert.Equal("MEDIA_PROVIDER_RESULT_UNVERIFIED", Assert.Single(repository.Releases).Code);
    }

    [Theory]
    [InlineData("container")]
    [InlineData("codec")]
    [InlineData("duration")]
    [InlineData("version")]
    [InlineData("bucket")]
    [InlineData("channels")]
    [InlineData("rate")]
    public async Task InvalidVerifiedOutputNeverBecomesAvailableAsync(string difference)
    {
        var repository = InFlight(Now.AddMinutes(-1), ProviderJobId);
        var valid = Variant(OutputKey);
        var other = difference switch
        {
            "container" => valid with { Container = "ogg" },
            "codec" => valid with { AudioCodec = "opus" },
            "duration" => valid with { Duration = 2m },
            "version" => valid with { Object = valid.Object with { VersionId = "null" } },
            "bucket" => valid with { Object = valid.Object with { Bucket = "other-bucket" } },
            "channels" => valid with { Channels = 2 },
            _ => valid with { SampleRate = 44100 }
        };
        var storage = new MediaAudioStorageStub { Verify = (_, _, _) => Task.FromResult(other) };
        var provider = new MediaTranscodingProviderStub
        {
            Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(ProviderJob(RequestKey, "complete", OutputKey))
        };

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.Single(storage.Verified);
        Assert.Empty(repository.Promotions);
        Assert.DoesNotContain(repository.States, state => state.FileState == "available");
    }

    [Fact]
    public async Task DefiniteFailureRecordsOneCorrelatedF13ResultWithTheFileFailureAsync()
    {
        var repository = new MediaAudioRepositoryStub();
        var source = repository.Source;
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub { Create = (_, _) => throw new MediaJobRejectedException(false) };
        var service = Service(repository, storage, provider);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));
        Assert.False(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Equal(source, repository.Source);
        var failure = Assert.Single(repository.Failures);
        var transition = Assert.Single(repository.States, state => state.FileState == "failed");
        Assert.True(failure.Transaction.Committed);
        Assert.Equal(transition.Transaction, failure.Transaction);
        Assert.Equal("record.media-preparation.failed.v1", failure.Failure.Type);
        var confirmation = MediaPreparationTestFixture.Confirmation;
        Assert.Equal(confirmation.EventId, failure.Failure.EventId);
        Assert.Equal(confirmation.CorrelationId, failure.Failure.CorrelationId);
        Assert.Equal(confirmation.MessageId, failure.Failure.CausationId);
        using var json = JsonDocument.Parse(failure.Failure.Payload);
        Assert.Equal("transcode", json.RootElement.GetProperty("stage").GetString());
        Assert.DoesNotContain(Bucket, failure.Failure.Payload);
        Assert.DoesNotContain(source!.ObjectKey, failure.Failure.Payload);
        Assert.Empty(repository.Promotions);
    }

    [Fact]
    public async Task DoesNotCreateWhenTheLookupUsesUpTheRemainingSubmissionBudgetAsync()
    {
        var repository = InFlight(Now.AddSeconds(-34));
        var clock = new EventTestTimeProvider { Now = Now };
        var provider = new MediaTranscodingProviderStub
        {
            Find = (_, _, _) =>
            {
                clock.Now += TimeSpan.FromSeconds(2);
                return Task.FromResult<MediaProviderJobModel?>(null);
            }
        };
        var service = new MediaAudioPreparationService(repository, new MediaAudioStorageStub(), provider, Settings, clock);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Single(provider.Searches);
        Assert.Empty(provider.Submissions);
        Assert.Equal("reconciling", repository.Job!.State);
    }

    [Fact]
    public async Task UnknownSubmissionAfterOneDayRemainsRecoverableForInterventionAsync()
    {
        var repository = InFlight(Now.AddHours(-24));
        var provider = new MediaTranscodingProviderStub();

        Assert.True(await Service(repository, new(), provider).PrepareNextAsync(CancellationToken.None));

        Assert.Single(provider.Searches);
        Assert.Empty(provider.Submissions);
        Assert.Equal(repository.Attempt!.Id, repository.Job!.ActiveAttemptId);
        Assert.Equal("unknown", repository.Attempt.State);
        var release = Assert.Single(repository.Releases);
        Assert.Equal(("reconciling", "MEDIA_RECONCILIATION_INTERVENTION_REQUIRED", Now.AddMinutes(5)),
            (release.State, release.Code, release.At));
        Assert.Empty(repository.Failures);
    }

    [Theory]
    [InlineData("error", 1, true)]
    [InlineData("canceled", 1, true)]
    [InlineData("error", 5, false)]
    public async Task ProviderTerminalFailureReleasesOnlyAConclusiveAttemptAsync(string state, int attempts, bool retries)
    {
        var repository = InFlight(Now.AddMinutes(-1), ProviderJobId);
        repository.Job = repository.Job! with { AttemptCount = attempts, LastAttemptNumber = attempts };
        var provider = new MediaTranscodingProviderStub
        {
            Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(ProviderJob(RequestKey, state))
        };

        Assert.True(await Service(repository, new(), provider).PrepareNextAsync(CancellationToken.None));

        Assert.Null(repository.Job!.ActiveAttemptId);
        Assert.Equal("failed", repository.Attempt!.State);
        Assert.Empty(provider.Submissions);
        var transition = Assert.Single(repository.States);
        if (retries)
        {
            Assert.Equal(("pending", "processing", "MEDIA_PROVIDER_JOB_FAILED"),
                (transition.JobState, transition.FileState, transition.Code));
            Assert.InRange(transition.RetryAt!.Value, Now.AddSeconds(5), Now.AddSeconds(6));
            Assert.Empty(repository.Failures);
        }
        else
        {
            Assert.Equal(("failed", "failed", "MEDIA_ATTEMPTS_EXHAUSTED"),
                (transition.JobState, transition.FileState, transition.Code));
            Assert.Equal(transition.Transaction, Assert.Single(repository.Failures).Transaction);
        }
    }

    [Fact]
    public async Task TransientOutputReadFailureKeepsTheKnownJobAndDoesNotResubmitAsync()
    {
        var repository = InFlight(Now.AddMinutes(-1), ProviderJobId);
        var storage = new MediaAudioStorageStub { Verify = (_, _, _) => throw new MediaVariantStorageException() };
        var provider = new MediaTranscodingProviderStub
        {
            Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(ProviderJob(RequestKey, "complete", OutputKey))
        };

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.Equal(ProviderJobId, repository.Attempt!.ProviderJobId);
        Assert.Equal("reconciling", repository.Job!.State);
        Assert.Equal("MEDIA_VARIANT_STORAGE_UNAVAILABLE", Assert.Single(repository.Releases).Code);
        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.Failures);
        Assert.Empty(provider.Submissions);
    }

    [Fact]
    public async Task CancellationDuringCreatePreservesItsDurableSubmissionIntentAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new MediaAudioRepositoryStub();
        var provider = new MediaTranscodingProviderStub
        {
            Create = (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new MediaProviderJobModel("", "", Guid.Empty, "", null));
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(repository, new(), provider).PrepareNextAsync(cancellation.Token));

        Assert.True(Assert.Single(repository.Submissions).Transaction.Committed);
        Assert.Equal("unknown", repository.Attempt!.State);
        Assert.Equal(repository.Attempt.Id, repository.Job!.ActiveAttemptId);
        Assert.Empty(repository.Failures);
    }

    [Fact]
    public async Task MissingConfirmedOriginalStopsBeforeStagingOrCreatingAsync()
    {
        var repository = new MediaAudioRepositoryStub { Source = null };
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub();

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.True(Assert.Single(repository.SourceReads).Locked);
        Assert.Empty(storage.Staged);
        Assert.Empty(provider.Submissions);
        Assert.Equal("MEDIA_SOURCE_INTEGRITY", Assert.Single(repository.States).Code);
        Assert.Single(repository.Failures);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("profile")]
    [InlineData("duration")]
    [InlineData("prefix")]
    public async Task IncorrectRecoveredPlanCannotReachMediaConvertAsync(string difference)
    {
        var repository = InFlight(Now.AddSeconds(-1));
        var job = repository.Job!;
        var plan = JsonSerializer.Deserialize<MediaAudioPlanModel>(job.Profile)!;
        var input = plan.Input!;
        if (difference == "prefix")
            repository.Attempt = repository.Attempt! with { OutputPrefix = OutputPrefix.Replace("701", "702", StringComparison.Ordinal) };
        plan = difference switch
        {
            "key" => plan with { Input = input with { Object = input.Object with { Key = OutputKey } } },
            "version" => plan with { Input = input with { Object = input.Object with { VersionId = "null" } } },
            "profile" => plan with { Profile = Profile with { SampleRate = 44100 } },
            "duration" => plan with { Input = input with { Duration = 0 } },
            _ => plan
        };
        repository.Job = job with { Profile = JsonSerializer.Serialize(plan) };
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub();

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.Equal("MEDIA_INPUT_RECONCILIATION_REQUIRED", Assert.Single(repository.Releases).Code);
        Assert.Equal(repository.Attempt!.Id, repository.Job.ActiveAttemptId);
        Assert.Empty(provider.Searches);
        Assert.Empty(provider.Reads);
        Assert.Empty(provider.Submissions);
        Assert.Empty(storage.Staged);
        Assert.Empty(repository.Failures);
    }

    [Theory]
    [InlineData("unavailable", "MEDIA_SOURCE_UNAVAILABLE", "processing")]
    [InlineData("integrity", "MEDIA_SOURCE_INTEGRITY", "failed")]
    [InlineData("invalid", "MEDIA_SOURCE_INVALID", "failed")]
    public async Task ClassifiesStagingFailureBeforeSubmissionAsync(string difference, string code, string fileState)
    {
        var repository = new MediaAudioRepositoryStub();
        var provider = new MediaTranscodingProviderStub();
        var storage = new MediaAudioStorageStub
        {
            Stage = (_, _, _) => throw difference switch
            {
                "unavailable" => (Exception)new MediaVariantStorageException(),
                "integrity" => new MediaSourceIntegrityException(),
                _ => new MediaOutputInvalidException()
            }
        };

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        var transition = Assert.Single(repository.States);
        Assert.Equal((code, fileState), (transition.Code, transition.FileState));
        Assert.Empty(repository.Inputs);
        Assert.Empty(repository.Submissions);
        Assert.Empty(provider.Submissions);
        if (fileState == "failed")
            Assert.Single(repository.Failures);
        else
            Assert.Empty(repository.Failures);
    }

    [Fact]
    public async Task TerminalLookupSchedulesAnImmediateAuthoritativeGetBeforeVerificationAsync()
    {
        var repository = InFlight(Now.AddMinutes(-2));
        var storage = new MediaAudioStorageStub();
        var provider = new MediaTranscodingProviderStub
        {
            Find = (_, _, _) => Task.FromResult<MediaProviderJobModel?>(ProviderJob(RequestKey, "complete", OutputKey)),
            Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(ProviderJob(RequestKey, "complete", OutputKey))
        };
        var service = Service(repository, storage, provider);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Empty(storage.Verified);
        Assert.Equal(Now, Assert.Single(repository.Releases).At);
        Assert.Equal(ProviderJobId, repository.Attempt!.ProviderJobId);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        Assert.Empty(provider.Submissions);
        Assert.Single(provider.Reads);
        Assert.Single(repository.Promotions);
    }

    [Fact]
    public async Task AssociatesTheFullProviderClaimWithItsLocallyStagedInputAndOutputAsync()
    {
        var repository = InFlight(Now.AddMinutes(-1), ProviderJobId);
        var plan = JsonSerializer.Deserialize<MediaAudioPlanModel>(repository.Job!.Profile)!;
        var completed = ProviderJob(RequestKey, "complete", OutputKey) with
        {
            JobRoleArn = Settings.JobRoleArn,
            ClientRequestToken = RequestKey.ToString("N"),
            InputUri = $"s3://{Bucket}/{plan.Input!.Object.Key}",
            OutputDestination = $"s3://{Bucket}/{OutputKey[..^4]}"
        };
        var provider = new MediaTranscodingProviderStub
        {
            Get = (_, _) => Task.FromResult<MediaProviderJobModel?>(completed)
        };
        var storage = new MediaAudioStorageStub();

        Assert.True(await Service(repository, storage, provider).PrepareNextAsync(CancellationToken.None));

        Assert.Single(storage.Verified);
        Assert.Single(repository.Promotions);
        Assert.Equal("available", Assert.Single(repository.States).FileState);
    }

    private static MediaAudioPreparationService Service(MediaAudioRepositoryStub repository, MediaAudioStorageStub storage,
        MediaTranscodingProviderStub provider) => new(repository, storage, provider, Settings, new EventTestTimeProvider { Now = Now });
}
