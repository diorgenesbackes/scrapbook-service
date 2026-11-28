using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Application.Diagnostics;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class MediaPreparationServiceTests
{
    private static readonly PhotoPreparationProfileModel Profile = new(2048, 400, 85);
    private readonly MediaPreparationRepositoryStub repository = new() { Eligible = Job() };
    private readonly MediaVariantStorageStub storage = new();
    private readonly PhotoVariantProcessorStub processor = new();
    private readonly EventTestTimeProvider clock = new() { Now = MediaPreparationTestFixture.Confirmation.ConfirmedAt.AddHours(2) };
    private MediaPreparationService Service => new(repository, storage, processor, clock);

    [Fact]
    public async Task StaysIdleWhenNoPhotoIsEligibleAsync()
    {
        repository.Eligible = null;

        Assert.False(await Service.PrepareNextAsync(CancellationToken.None));

        Assert.Empty(repository.Starts);
        Assert.Empty(storage.Reads);
        Assert.Empty(storage.Stores);
        Assert.Empty(processor.Calls);
    }

    [Fact]
    public async Task PromotesBothVerifiedVersionsInTheSameAvailabilityTransactionAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var verifyThumbnail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.VerifyAsync = async (key, result, token) =>
        {
            Assert.True(token.CanBeCanceled);
            Assert.True(Assert.Single(repository.Transactions).Committed);
            if (key.EndsWith("/thumbnail.jpg", StringComparison.Ordinal))
            {
                await verifyThumbnail.Task.WaitAsync(token);
            }
            return result;
        };

        var pending = Service.PrepareNextAsync(cancellation.Token);

        Assert.Equal(2, storage.Stores.Count);
        Assert.False(pending.IsCompleted);
        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.States);
        verifyThumbnail.SetResult();
        Assert.True(await pending);

        var start = Assert.Single(repository.Starts);
        var read = Assert.Single(storage.Reads);
        Assert.Equal(("private-media", "submissions/original/pinned", "confirmed-version"),
            (read.Source.Bucket, read.Source.ObjectKey, read.Source.VersionId));
        Assert.Equal(SHA256.HashData(storage.Original), read.Source.Sha256);
        Assert.True(read.Token.CanBeCanceled);
        Assert.StartsWith("submissions/derived/701/", start.Prefix);
        Assert.Equal(clock.Now.AddMinutes(2), start.Until);
        var convert = Assert.Single(processor.Calls);
        Assert.Equal((storage.Original, Profile), (convert.Original, convert.Profile));
        Assert.True(convert.Token.CanBeCanceled);
        var available = Assert.Single(repository.States);
        Assert.Equal(("completed", "available"), (available.JobState, available.FileState));
        Assert.Collection(repository.Promotions,
            entry => AssertPromotion(entry, start.Prefix, "image", "image-version", available.Transaction),
            entry => AssertPromotion(entry, start.Prefix, "thumbnail", "thumb-version", available.Transaction));
        Assert.Same(available.Transaction, Assert.Single(repository.Ends).Transaction);
        Assert.Same(available.Transaction, repository.Transactions[1]);
        Assert.True(available.Transaction.Committed);
        Assert.Equal(cancellation.Token, available.Transaction.CommitToken);
        var leaseCheck = Assert.Single(repository.LeaseChecks);
        Assert.Equal((start.Lease, start.Job.Version + 1, start.Prefix, cancellation.Token),
            (leaseCheck.Work.Lease, leaseCheck.Work.Job.Version, leaseCheck.Work.OutputPrefix, leaseCheck.Token));
        Assert.Empty(repository.Failures);
    }

    [Theory]
    [InlineData(-1, 1L, false)]
    [InlineData(0, 1L, true)]
    [InlineData(1, 0L, false)]
    public async Task RecordsReadinessOnlyAfterTheCommitOfBothPhotoVariantsAsync(int offsetMilliseconds, long withinTarget,
        bool isHistorical)
    {
        var telemetry = new MediaProcessingTelemetry();
        var job = Job();
        var confirmedAt = job.ConfirmedAt;
        var admittedAt = confirmedAt.AddSeconds(50);
        job = job with { IsHistorical = isHistorical, AdmittedAt = admittedAt };
        repository.Eligible = isHistorical ? null : job;
        repository.HistoricalEligible = isHistorical ? job : null;
        var start = isHistorical ? admittedAt : confirmedAt;
        clock.Now = start.AddSeconds(295);
        var readySeconds = 300 + offsetMilliseconds / 1000.0;
        repository.BeforeCommitAsync = _ =>
        {
            Assert.DoesNotContain(telemetry.CollectInterval(), measurement => measurement.Stage == "ready");
            if (repository.Promotions.Count == 2) clock.Now = start.AddMilliseconds(300_000 + offsetMilliseconds);
            return Task.CompletedTask;
        };
        var service = new MediaPreparationService(repository, storage, processor, clock, telemetry: telemetry);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        var ready = Assert.Single(telemetry.CollectInterval(), measurement => measurement.Stage == "ready");
        Assert.Equal(("photo", isHistorical ? "historical" : "recent", "available"), (ready.MediaType, ready.Cohort, ready.Outcome));
        Assert.Equal(1, ready.SampleCount);
        Assert.Equal(readySeconds, ready.DurationSumSeconds, 6);
        Assert.Equal(readySeconds, ready.DurationMaximumSeconds, 6);
        Assert.Equal(withinTarget, ready.WithinTargetCount);
        Assert.Equal(withinTarget, ready.DurationBuckets["300"]);
        Assert.Equal(0, ready.DurationBuckets["120"]);
        Assert.Equal(1, ready.DurationBuckets["600"]);
        Assert.Equal(1, ready.DurationBuckets["+Inf"]);
        Assert.Empty(telemetry.CollectInterval());
    }

    [Fact]
    public async Task DoesNotRecordReadinessWhenTheAvailabilityCommitFailsAsync()
    {
        var telemetry = new MediaProcessingTelemetry();
        repository.BeforeCommitAsync = _ => repository.Promotions.Count == 2
            ? Task.FromException(new PostgresUnavailableException())
            : Task.CompletedTask;
        var service = new MediaPreparationService(repository, storage, processor, clock, telemetry: telemetry);

        await Assert.ThrowsAsync<PostgresUnavailableException>(() => service.PrepareNextAsync(CancellationToken.None));

        Assert.False(Assert.Single(repository.States).Transaction.Committed);
        Assert.DoesNotContain(telemetry.CollectInterval(), measurement => measurement.Stage == "ready");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportsATerminalFailureWithoutTreatingAPendingRetryAsReadyOrFailedAsync(bool terminal)
    {
        var telemetry = new MediaProcessingTelemetry();
        if (terminal)
        {
            storage.Original = [0];
        }
        else
        {
            storage.VerifyAsync = (_, _, _) => Task.FromException<MediaStoredVariantModel>(new MediaVariantStorageException());
        }
        var service = new MediaPreparationService(repository, storage, processor, clock, telemetry: telemetry);

        Assert.True(await service.PrepareNextAsync(CancellationToken.None));

        var measurements = telemetry.CollectInterval();
        Assert.DoesNotContain(measurements, measurement => measurement.Stage == "ready");
        var terminalFailures = measurements.Where(measurement => measurement.Stage == "failure" && measurement.Outcome == "failed");
        if (terminal)
        {
            var failed = Assert.Single(terminalFailures);
            Assert.Equal(("photo", "recent", 1L, 0L), (failed.MediaType, failed.Cohort, failed.SampleCount, failed.WithinTargetCount));
            Assert.True(Assert.Single(repository.States).Transaction.Committed);
        }
        else
        {
            Assert.Empty(terminalFailures);
            Assert.Equal("pending", Assert.Single(repository.States).JobState);
        }
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    public async Task KeepsTheFileProcessingWhenTheSecondVerificationNeedsARetryAsync(int attempts, int interval)
    {
        repository.Eligible = Job() with { AttemptCount = attempts, LastAttemptNumber = attempts };
        storage.VerifyAsync = (key, result, _) => key.EndsWith("/thumbnail.jpg", StringComparison.Ordinal)
            ? Task.FromException<MediaStoredVariantModel>(new MediaVariantStorageException()) : Task.FromResult(result);

        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));

        Assert.Equal(2, storage.Stores.Count);
        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.Failures);
        var retry = Assert.Single(repository.States);
        Assert.Equal(("pending", "processing", "MEDIA_STORAGE_UNAVAILABLE"), (retry.JobState, retry.FileState, retry.Code));
        Assert.InRange(retry.RetryAt!.Value, clock.Now.AddSeconds(interval), clock.Now.AddSeconds(interval + 1));
        Assert.True(retry.Transaction.Committed);
        Assert.Equal("failed", Assert.Single(repository.Ends).State);
    }

    [Theory]
    [InlineData("foreign-key")]
    [InlineData("wrong-checksum")]
    [InlineData("unversioned")]
    public async Task RejectsASecondStoredVariantWithoutTheRequiredIdentityAsync(string defect)
    {
        storage.VerifyAsync = (key, result, _) => Task.FromResult(key.EndsWith("/thumbnail.jpg", StringComparison.Ordinal)
            ? defect switch
            {
                "foreign-key" => result with { Key = "submissions/derived/702/other/thumbnail.jpg" },
                "wrong-checksum" => result with { Sha256 = new byte[32] },
                "unversioned" => result with { VersionId = "null" },
                _ => throw new ArgumentOutOfRangeException(nameof(defect))
            }
            : result);

        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));

        Assert.Empty(repository.Promotions);
        AssertFailure("verify", "MEDIA_VARIANT_INVALID");
    }

    [Fact]
    public async Task RejectsOutOfProfileDimensionsBeforeAnyOutputIsStoredAsync()
    {
        var variants = processor.Variants;
        processor.Variants = variants with { Thumbnail = variants.Thumbnail with { Width = 401 } };

        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));

        Assert.Empty(storage.Stores);
        Assert.Empty(repository.Promotions);
        AssertFailure("verify", "MEDIA_VARIANT_INVALID");
    }

    [Fact]
    public async Task RejectsAnOriginalWithTheWrongChecksumBeforeCallingTheProcessorAsync()
    {
        storage.Original = [1, 2, 3, 4];

        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));

        Assert.Empty(processor.Calls);
        Assert.Empty(storage.Stores);
        AssertFailure("prepare", "MEDIA_SOURCE_INTEGRITY");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReplacedLeaseCanNeitherPublishNorFailTheCurrentAttemptAsync(bool secondWriteFails)
    {
        storage.VerifyAsync = (key, result, _) =>
        {
            if (key.EndsWith("/image.jpg", StringComparison.Ordinal)) return Task.FromResult(result);
            repository.RetainsLease = false;
            return secondWriteFails ? Task.FromException<MediaStoredVariantModel>(new MediaVariantStorageException()) : Task.FromResult(result);
        };

        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));

        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.States);
        Assert.Empty(repository.Ends);
        Assert.Empty(repository.Failures);
        Assert.Single(repository.LeaseChecks);
        Assert.False(repository.Transactions[1].Committed);
    }

    [Fact]
    public async Task ANewServiceInstanceResumesAnInterruptedAttemptWithASeparatePrefixAsync()
    {
        using var cancellation = new CancellationTokenSource();
        storage.VerifyAsync = (key, result, token) =>
        {
            if (key.EndsWith("/thumbnail.jpg", StringComparison.Ordinal)) cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.PrepareNextAsync(cancellation.Token));
        Assert.Empty(repository.States);
        Assert.Empty(repository.Ends);
        Assert.Empty(repository.Promotions);
        Assert.Empty(repository.Failures);
        var interrupted = Assert.Single(repository.Starts);
        Assert.True(interrupted.Transaction.Committed);

        clock.Now = interrupted.Until.AddSeconds(1);
        repository.Eligible = interrupted.Job with { State = "processing", ActiveAttemptId = 7, LastAttemptNumber = 1, AttemptCount = 1 };
        repository.Source = repository.Source! with { ProcessingState = "processing" };
        storage.VerifyAsync = null;
        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));

        var replacement = repository.Starts[1];
        Assert.Equal(2, replacement.Number);
        Assert.NotEqual(interrupted.RequestKey, replacement.RequestKey);
        Assert.NotEqual(interrupted.Prefix, replacement.Prefix);
        var closed = repository.Ends[0];
        Assert.Equal((7L, "failed", "MEDIA_ATTEMPT_INTERRUPTED"), (closed.Id, closed.State, closed.Code));
        Assert.Same(replacement.Transaction, closed.Transaction);
        Assert.All(repository.Promotions, entry => Assert.StartsWith(replacement.Prefix, entry.Variant.Storage.Key));
        Assert.Equal("available", Assert.Single(repository.States).FileState);
    }

    [Fact]
    public async Task EmitsAStableTerminalIdentityForTheSameRetryCycleAsync()
    {
        repository.Eligible = Job() with { AttemptCount = 4, LastAttemptNumber = 4 };
        storage.VerifyAsync = (_, _, _) => Task.FromException<MediaStoredVariantModel>(new MediaVariantStorageException());

        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));
        var originalFailure = AssertFailure("verify", "MEDIA_ATTEMPTS_EXHAUSTED");
        Assert.Empty(repository.Promotions);

        repository.Eligible = Job() with { AttemptCount = 5, LastAttemptNumber = 5, ActiveAttemptId = 9, State = "processing" };
        clock.Now = clock.Now.AddMinutes(2);
        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));
        Assert.Equal(originalFailure.MessageId, repository.Failures[1].Result.MessageId);
        Assert.Single(repository.Starts);

        repository.Eligible = repository.Eligible with { RetryCycle = 2 };
        Assert.True(await Service.PrepareNextAsync(CancellationToken.None));
        Assert.NotEqual(originalFailure.MessageId, repository.Failures[2].Result.MessageId);
    }

    [Fact]
    public async Task ReadsTheProjectionUsingAllThreeScopeIdentifiersAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var confirmation = MediaPreparationTestFixture.Confirmation;
        repository.Projection = new(confirmation.FileId, "processing", 1, []);

        var file = await Service.GetFileAsync(confirmation.EventId, confirmation.RecordId, confirmation.FileId, cancellation.Token);

        Assert.Equal(repository.Projection, file);
        Assert.Equal((confirmation.EventId, confirmation.RecordId, confirmation.FileId, cancellation.Token), Assert.Single(repository.Reads));
        Assert.Empty(file!.Variants);
    }

    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    public async Task GivesNewConfirmationsPriorityAndHonorsHistoricalCapacityAsync(bool newWork, bool allowHistorical,
        bool preferHistorical, bool takesHistory)
    {
        using var cancellation = new CancellationTokenSource();
        repository.Eligible = newWork ? Job() : null;
        repository.HistoricalEligible = Job() with { Id = Job().Id + 1 };

        Assert.Equal(newWork || takesHistory, await Service.PrepareNextAsync(allowHistorical, preferHistorical, cancellation.Token));

        var historicalFirst = allowHistorical && preferHistorical;
        Assert.Equal((historicalFirst, cancellation.Token), repository.EligibilityChecks[0]);
        if (takesHistory)
        {
            if (historicalFirst) Assert.Single(repository.EligibilityChecks);
            else Assert.Equal((true, cancellation.Token), repository.EligibilityChecks[1]);
            Assert.Equal(repository.HistoricalEligible, Assert.Single(repository.Starts).Job);
        }
        else
        {
            Assert.Single(repository.EligibilityChecks);
            if (newWork) Assert.Equal(repository.Eligible, Assert.Single(repository.Starts).Job);
            else Assert.Empty(repository.Starts);
        }
    }

    [Theory]
    [InlineData("photo", "available", "available", "image", "thumbnail")]
    [InlineData("photo", "available", "failed", "image", null)]
    [InlineData("photo", "processing", "processing", "image", null)]
    [InlineData("photo", "not_requested", "pending", "image", null)]
    [InlineData("video", "available", "available", "playback", null)]
    [InlineData("audio", "available", "available", "playback", null)]
    [InlineData("video", "available", "failed", "image", null)]
    public async Task ReturnsVersionedReferencesOnlyForACompleteReadyProfileAsync(string kind, string state, string expectedState,
        string firstVariant, string? secondVariant)
    {
        var confirmation = MediaPreparationTestFixture.Confirmation;
        var first = new MediaVariantReferenceModel(firstVariant, 801, "pinned-first-version", "image/jpeg");
        var second = new MediaVariantReferenceModel(secondVariant ?? "", 802, "pinned-second-version", "image/jpeg");
        MediaVariantReferenceModel[] variants = secondVariant is null ? [first] : [first, second];
        repository.Projection = new(confirmation.FileId, state, 1, variants, kind);

        var file = await Service.GetFileAsync(confirmation.EventId, confirmation.RecordId, confirmation.FileId, CancellationToken.None);

        Assert.NotNull(file);
        Assert.Equal((confirmation.FileId, kind, 1, expectedState), (file.FileId, file.Kind, file.ProfileVersion, file.State));
        Assert.Equal(expectedState == "available" ? variants : [], file.Variants);
    }

    private static void AssertPromotion(
        (PhotoPreparationWorkModel Work, PreparedPhotoVariantModel Variant, EventWriteTestTransaction Transaction) entry,
        string prefix, string kind, string version, EventWriteTestTransaction transaction)
    {
        Assert.Equal(kind, entry.Variant.Kind);
        Assert.Equal(prefix + kind + ".jpg", entry.Variant.Storage.Key);
        Assert.Equal(version, entry.Variant.Storage.VersionId);
        Assert.Equal(701, entry.Work.Job.FileId);
        Assert.Same(transaction, entry.Transaction);
    }

    private MediaFailureEntity AssertFailure(string stage, string code)
    {
        var failed = Assert.Single(repository.States);
        Assert.Equal(("failed", "failed", "failed", code), (failed.JobState, failed.FileState, failed.VariantState, failed.Code));
        Assert.Null(failed.RetryAt);
        Assert.True(failed.Transaction.Committed);
        var (failure, transaction) = Assert.Single(repository.Failures);
        Assert.Same(failed.Transaction, transaction);
        var confirmation = MediaPreparationTestFixture.Confirmation;
        Assert.NotEqual(Guid.Empty, failure.MessageId);
        Assert.Equal("record.media-preparation.failed.v1", failure.Type);
        Assert.Equal((confirmation.EventId, confirmation.MessageId, confirmation.CorrelationId, clock.Now),
            (failure.EventId, failure.CausationId, failure.CorrelationId, failure.OccurredAt));
        using var payload = JsonDocument.Parse(failure.Payload);
        Assert.Equal("560", payload.RootElement.GetProperty("recordId").GetString());
        Assert.Equal("701", payload.RootElement.GetProperty("fileId").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("profileVersion").GetInt32());
        Assert.Equal(stage, payload.RootElement.GetProperty("stage").GetString());
        Assert.Equal(code, payload.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("private-media", failure.Payload);
        Assert.DoesNotContain("confirmed-version", failure.Payload);
        return failure;
    }

    private static MediaJobEntity Job()
    {
        var confirmation = MediaPreparationTestFixture.Confirmation;
        return new(3, confirmation.EventId, confirmation.RecordId, confirmation.FileId, 1, 4, "pending", 0, 1,
            confirmation.MessageId, confirmation.CorrelationId, confirmation.ConfirmedAt, null, 0, JsonSerializer.Serialize(Profile));
    }
}
