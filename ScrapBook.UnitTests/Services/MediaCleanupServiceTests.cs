using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class MediaCleanupServiceTests
{
    private const string Bucket = "private-media";
    private const string PinnedVersion = "confirmed-generated-version";
    private const string Revision = "cfe74b654125ebfc8fdf77f2d47c18702f26788b";
    private static readonly Guid RequestKey = Guid.Parse("47a30fd4-c931-4fbb-84b6-440cd832b9f2");
    private static readonly MediaRecoveryBatchCommand Command = new(200, 120, 3, "INC-708", Revision, false);
    private readonly MediaRecoveryAccessRepositoryStub audit = new();
    private readonly MediaCleanupRepositoryStub repository = new();
    private readonly MediaCleanupStorageStub storage = new();
    private readonly EventTestTimeProvider clock = new();
    private MediaCleanupService Service => new(new(audit), audit, repository, storage, clock, Bucket);

    [Fact]
    public async Task RejectsAnUnauthorizedCallerBeforeLookingUpAnyArtifactsAsync()
    {
        audit.Operator = null;
        var result = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("MEDIA_RECOVERY_FORBIDDEN", Command.AfterId, 0, 0, 0, 0), result);
        Assert.Empty(repository.Pages);
        Assert.Empty(storage.Lookups);
        Assert.Empty(storage.Deletions);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task RejectsAnOversizedPageBeforeLookingUpTheCallerAsync()
    {
        var result = await Service.ScanAsync(Command with { Limit = 101 }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("MEDIA_RECOVERY_INVALID_ARGUMENT", Command.AfterId, 0, 0, 0, 0), result);
        Assert.Empty(audit.OperatorReads);
        Assert.Empty(repository.Pages);
    }

    [Fact]
    public async Task PreviewsOneEventAndReturnsTheLastExaminedArtifactWithoutDeletingOrAuditingAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var photo = Photo();
        repository.Candidates = [photo, photo with { Id = photo.Id + 1, ObjectKey = "submissions/original/pinned" }];

        var result = await Service.ScanAsync(Command, cancellation.Token);

        Assert.Equal(new MediaRecoveryBatchResult("OK", photo.Id + 1, 2, 0, 1, 0), result);
        Assert.Equal((Command.EventId, Command.AfterId, Command.Limit, clock.Now.AddHours(-24), cancellation.Token),
            Assert.Single(repository.Pages));
        Assert.Empty(repository.Claims);
        Assert.Empty(audit.Entries);
        Assert.Empty(storage.Lookups);
        Assert.Empty(storage.Deletions);
    }

    [Fact]
    public async Task KeepsTheIncomingCursorWhenNoArtifactIsEligibleAsync()
    {
        var result = await Service.ScanAsync(Command, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", Command.AfterId, 0, 0, 0, 0), result);
        Assert.Empty(repository.Transactions);
    }

    [Fact]
    public async Task DoesNotClaimTheNextObjectWhenTheExplicitSelectionIsIneligibleAsync()
    {
        var selected = Photo();
        repository.Candidates = [selected with { Id = selected.Id + 1 }];

        var result = await Service.ScanAsync(Command with { Apply = true, CleanupObjectId = selected.Id }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", Command.AfterId, 0, 0, 0, 0), result);
        Assert.Equal((Command.EventId, selected.Id - 1, 1, clock.Now.AddHours(-24), CancellationToken.None),
            Assert.Single(repository.Pages));
        Assert.Empty(repository.Claims);
        Assert.Empty(storage.Deletions);
    }

    [Fact]
    public async Task ClaimsOnlyTheExplicitlySelectedObjectAsync()
    {
        var selected = Photo();
        repository.Candidates = [selected];

        var result = await Service.ScanAsync(Command with { Apply = true, CleanupObjectId = selected.Id }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", selected.Id, 1, 1, 0, 0), result);
        Assert.Equal(selected, Assert.Single(repository.Claims).Candidate);
        Assert.Equal(selected.ObjectKey, Assert.Single(storage.Deletions).Key);
    }

    [Theory]
    [InlineData("photo", "failed", false)]
    [InlineData("audio", "failed", false)]
    [InlineData("video", "failed", false)]
    [InlineData("audio", "succeeded", true)]
    [InlineData("video", "succeeded", true)]
    public async Task AuditsAndClaimsAnEligibleArtifactBeforeStorageAndCompletesTheSameClaimAsync(string media, string state,
        bool intermediate)
    {
        using var cancellation = new CancellationTokenSource();
        var artifact = media == "photo" ? Photo() : Transcode(media, state, intermediate);
        if (intermediate) artifact = artifact with { VersionId = PinnedVersion, LastModified = clock.Now.AddHours(-24) };
        repository.Candidates = [artifact];
        storage.DeleteAsync = token =>
        {
            Assert.True(token.CanBeCanceled);
            var transaction = Assert.Single(repository.Transactions);
            Assert.True(transaction.Committed);
            Assert.True(transaction.Disposed);
            Assert.Equal(cancellation.Token, transaction.CommitToken);
            Assert.Equal("cleanup_claimed", Assert.Single(audit.Entries).Entry.ReasonCode);
            return Task.CompletedTask;
        };

        var result = await Service.ScanAsync(Command with { Apply = true }, cancellation.Token);

        Assert.Equal(new MediaRecoveryBatchResult("OK", artifact.Id, 1, 1, 0, 0), result);
        var claim = Assert.Single(repository.Claims);
        Assert.Equal((Command.EventId, artifact, clock.Now.AddHours(-24), clock.Now, cancellation.Token),
            (claim.EventId, claim.Candidate, claim.Cutoff, claim.Now, claim.Token));
        Assert.NotEqual(Guid.Empty, claim.Lease);
        if (intermediate) Assert.Empty(storage.Lookups);
        else
        {
            var lookup = Assert.Single(storage.Lookups);
            Assert.Equal((Bucket, artifact.ObjectKey, clock.Now.AddHours(-24)), (lookup.Bucket, lookup.Key, lookup.Cutoff));
        }
        var deletion = Assert.Single(storage.Deletions);
        Assert.Equal(intermediate ? PinnedVersion : storage.ExpiredVersion, claim.VersionId);
        Assert.Equal((Bucket, artifact.ObjectKey, claim.VersionId), (deletion.Bucket, deletion.Key, deletion.VersionId));
        Assert.Equal((artifact.Id, repository.ClaimVersion!.Value, claim.Lease, clock.Now, cancellation.Token),
            Assert.Single(repository.Completions));
        Assert.Collection(audit.Entries,
            item => AssertAudit(item, artifact, "success", "cleanup_claimed", cancellation.Token),
            item => AssertAudit(item, artifact, "success", "expired_attempt_version_removed", cancellation.Token));
        Assert.Equal(2, repository.Transactions.Count);
        Assert.True(repository.Transactions[1].Committed);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("different file")]
    [InlineData("different bucket")]
    [InlineData("different attempt")]
    [InlineData("ready photo")]
    [InlineData("ready video")]
    [InlineData("wrong provider")]
    [InlineData("wrong extension")]
    public async Task LeavesOriginalsReadyVariantsAndOtherAttemptObjectsOutsideStorageCleanupAsync(string scenario)
    {
        var artifact = Photo();
        artifact = scenario switch
        {
            "original" => artifact with { ObjectKey = "submissions/original/pinned" },
            "different file" => artifact with { FileId = artifact.FileId + 1 },
            "different bucket" => artifact with { Bucket = "another-media" },
            "different attempt" => artifact with { RequestKey = Guid.NewGuid() },
            "ready photo" => artifact with { AttemptState = "succeeded" },
            "ready video" => Transcode("video", "succeeded", false),
            "wrong provider" => artifact with { Provider = "mediaconvert" },
            "wrong extension" => artifact with { ObjectKey = artifact.OutputPrefix + "image.png" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        repository.Candidates = [artifact];

        var result = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", artifact.Id, 1, 0, 1, 0), result);
        Assert.Empty(repository.Claims);
        Assert.Empty(storage.Lookups);
        Assert.Empty(storage.Deletions);
        AssertAudit(Assert.Single(audit.Entries), artifact, "rejected", "artifact_scope_mismatch", CancellationToken.None);
    }

    [Fact]
    public async Task PreservesAnArtifactWhenTheScopedClaimNoLongerProvesItIsUnreferencedAsync()
    {
        var artifact = Photo();
        repository.Candidates = [artifact];
        repository.ClaimVersion = null;

        var result = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", artifact.Id, 1, 0, 0, 1), result);
        Assert.Single(repository.Claims);
        Assert.Single(storage.Lookups);
        Assert.Empty(storage.Deletions);
        Assert.Empty(audit.Entries);
        Assert.False(Assert.Single(repository.Transactions).Committed);
    }

    [Theory]
    [InlineData("storage unavailable")]
    [InlineData("completion superseded")]
    public async Task DefersTheArtifactWithoutRecordingCompletedDeletionWhenItCannotFinishAsync(string scenario)
    {
        var artifact = Photo();
        repository.Candidates = [artifact];
        switch (scenario)
        {
            case "storage unavailable": storage.DeleteAsync = _ => throw new MediaVariantStorageException(); break;
            case "completion superseded": repository.Completes = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var result = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", artifact.Id, 1, 0, 0, 1), result);
        AssertAudit(Assert.Single(audit.Entries), artifact, "success", "cleanup_claimed", CancellationToken.None);
        if (scenario == "completion superseded") Assert.False(repository.Transactions[1].Committed);
        else Assert.Empty(repository.Completions);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("null", false)]
    [InlineData(" ", false)]
    [InlineData(PinnedVersion, true)]
    public async Task DefersWhenAnExpiredConcreteVersionCannotBeProvedAsync(string? version, bool tooNew)
    {
        var artifact = Photo();
        if (tooNew) artifact = artifact with { VersionId = version, LastModified = clock.Now.AddHours(-24).AddSeconds(1) };
        repository.Candidates = [artifact];
        storage.ExpiredVersion = version;

        var result = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", artifact.Id, 1, 0, 0, 1), result);
        Assert.Empty(repository.Claims);
        Assert.Empty(storage.Deletions);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task PropagatesCallerCancellationDuringStorageWithoutRecordingCompletionAsync()
    {
        using var cancellation = new CancellationTokenSource();
        repository.Candidates = [Photo()];
        storage.DeleteAsync = token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service.ScanAsync(Command with { Apply = true }, cancellation.Token));

        Assert.True(Assert.Single(repository.Transactions).Committed);
        Assert.Empty(repository.Completions);
    }

    private static MediaCleanupObjectModel Photo()
    {
        var prefix = $"submissions/derived/701/{RequestKey:N}/";
        return new(125, 701, 3, Bucket, prefix + "image.jpg", "local", "failed", RequestKey, prefix, "photo");
    }

    private static MediaCleanupObjectModel Transcode(string kind, string state, bool intermediate)
    {
        var suffix = $"701/{RequestKey:N}/";
        var prefix = "submissions/transcode-output/" + suffix;
        var key = intermediate
            ? "submissions/transcode-input/" + suffix + "input"
            : prefix + (kind == "audio" ? "playback.m4a" : "playback.mp4");
        return new(125, 701, 3, Bucket, key, "mediaconvert", state, RequestKey, prefix, kind);
    }

    private static void AssertAudit((MediaRecoveryAuditModel Entry, CancellationToken Token) item, MediaCleanupObjectModel artifact,
        string outcome, string reason, CancellationToken token)
    {
        var entry = item.Entry;
        Assert.Equal(("scrapbook_media_operator", Command.EventId, (long?)artifact.FileId, "media.cleanup", outcome, reason),
            (entry.Operator, entry.EventId, entry.FileId, entry.Action, entry.Outcome, entry.ReasonCode));
        Assert.Equal((Command.Incident, Command.Revision, artifact.RequestKey, token),
            (entry.Incident, entry.Revision, entry.CorrelationId, item.Token));
    }
}
