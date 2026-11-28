using ScrapBook.Application.Codecs;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class MediaPreparationRecoveryServiceTests
{
    private const string Incident = "INC-708";
    private const string Revision = "cfe74b654125ebfc8fdf77f2d47c18702f26788b";
    private static readonly MediaConfirmationModel Confirmation = MediaPreparationTestFixture.Confirmation;
    private static readonly MediaReopenCommand ReopenRequest = new(Confirmation.EventId, Confirmation.FileId, 1, 2, Incident, Revision, false);
    private static readonly MediaRedriveCommand RedriveRequest = new(Confirmation.EventId, Confirmation.MessageId, Incident, Revision, false);
    private readonly MediaPreparationRecoveryRepositoryStub repository = new() { Fact = MediaPreparationTestFixture.Outbox() };
    private readonly MediaAdmissionRepositoryStub admission = new();
    private readonly MediaRecoveryAccessRepositoryStub audit = new();
    private readonly MediaHistoryRepositoryStub history = new();
    private readonly OutboxTestRepository historyOutbox = new();
    private readonly EventTestTimeProvider clock = new() { Now = Confirmation.ConfirmedAt.AddDays(30) };
    private MediaPreparationRecoveryService Service => new(repository, admission, audit, new(audit), clock,
        new(history, admission, historyOutbox, audit, new(audit), clock));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeniesOperationsBeforeReadingEventFilesForAnUnauthorizedPrincipalAsync(bool reopen)
    {
        using var cancellation = new CancellationTokenSource();
        audit.Operator = null;

        var result = reopen
            ? await Service.ReopenAsync(ReopenRequest with { Apply = true }, cancellation.Token)
            : await Service.RedriveAsync(RedriveRequest with { Apply = true }, cancellation.Token);

        Assert.Equal(new MediaRecoveryResult("MEDIA_RECOVERY_FORBIDDEN", false), result);
        Assert.Equal(cancellation.Token, Assert.Single(audit.OperatorReads));
        Assert.Empty(repository.JobReads);
        Assert.Empty(repository.FactReads);
        Assert.Empty(admission.SourceReads);
        Assert.Empty(audit.Entries);
        var transaction = Assert.Single(admission.Transactions);
        Assert.True(transaction.Disposed);
        Assert.False(transaction.Committed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsInvalidCorrectiveEvidenceBeforeReadingOperatorOrEventAsync(bool reopen)
    {
        var result = reopen
            ? await Service.ReopenAsync(ReopenRequest with { Revision = new string('0', 40) }, CancellationToken.None)
            : await Service.RedriveAsync(RedriveRequest with { Incident = "invalid incident" }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult("MEDIA_RECOVERY_INVALID_ARGUMENT", false), result);
        Assert.Empty(audit.OperatorReads);
        Assert.Empty(admission.Transactions);
    }

    [Fact]
    public async Task PreviewsAnEligibleFailedCycleWithoutChangingWorkAsync()
    {
        SetFailedJob();

        var result = await Service.ReopenAsync(ReopenRequest, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REOPEN_PREVIEW", true), result);
        Assert.Empty(repository.Reopened);
        Assert.Equal(2, repository.Job!.RetryCycle);
        Assert.Equal("failed", repository.Job.State);
    }

    [Theory]
    [InlineData("photo")]
    [InlineData("audio")]
    [InlineData("video")]
    public async Task AppliesOnlyOneReopeningForTheReviewedCycleAndPreservesTheWorkIdentityAsync(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var prior = SetFailedJob(kind);
        repository.Fact = null;
        var command = ReopenRequest with { Apply = true };

        var result = await Service.ReopenAsync(command, cancellation.Token);
        var repeated = await Service.ReopenAsync(command, cancellation.Token);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REOPEN_APPLIED", true), result);
        Assert.Equal(new MediaRecoveryResult("MEDIA_REOPEN_JOB_INELIGIBLE", false), repeated);
        var reapplied = Assert.Single(repository.Reopened);
        Assert.Equal((Confirmation.EventId, prior, 1, 3, clock.Now, cancellation.Token),
            (reapplied.EventId, reapplied.Job, reapplied.ProfileVersion, reapplied.NextCycle, reapplied.Now, reapplied.Token));
        Assert.Equal(kind == "photo" ? ["image", "thumbnail"] : new[] { "playback" }, reapplied.Variants);
        Assert.Equal((Confirmation.EventId, Confirmation.FileId, 1, true, cancellation.Token), repository.JobReads[0]);
        Assert.Equal((Confirmation, true, cancellation.Token), Assert.Single(admission.SourceReads));
        Assert.Equal((Confirmation.EventId, Confirmation.FileId, 1, cancellation.Token), Assert.Single(repository.VariantReads));
        Assert.Empty(admission.Deliveries);
        Assert.Empty(admission.Jobs);
        Assert.Collection(audit.Entries,
            item => AssertAudit(item, "media.reopen", "success", result.Code, 3, cancellation.Token),
            item => AssertAudit(item, "media.reopen", "rejected", repeated.Code, 3, cancellation.Token));
        Assert.All(admission.Transactions, transaction =>
        {
            Assert.True(transaction.Committed);
            Assert.Equal(cancellation.Token, transaction.CommitToken);
        });
    }

    [Fact]
    public async Task RejectsReopeningWhenTheSelectedWorkDoesNotExistAsync()
    {
        var result = await Service.ReopenAsync(ReopenRequest with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REOPEN_JOB_NOT_FOUND", false), result);
        Assert.Empty(admission.SourceReads);
        Assert.Empty(repository.Reopened);
    }

    [Theory]
    [InlineData("completed", 2, false, false, 7)]
    [InlineData("failed", 3, false, false, 7)]
    [InlineData("failed", 2, true, false, 7)]
    [InlineData("failed", 2, false, true, 7)]
    [InlineData("failed", 2, false, false, int.MaxValue)]
    [InlineData("failed", int.MaxValue, false, false, 7)]
    public async Task LeavesActiveCompletedAndExhaustedWorkUntouchedAsync(string state, int cycle, bool lease,
        bool activeAttempt, int lastAttempt)
    {
        repository.Job = SetFailedJob() with
        {
            State = state,
            RetryCycle = cycle,
            HasLease = lease,
            HasActiveAttempt = activeAttempt,
            LastAttemptNumber = lastAttempt
        };
        var expected = cycle == int.MaxValue ? cycle : ReopenRequest.ExpectedCycle;

        var result = await Service.ReopenAsync(ReopenRequest with { Apply = true, ExpectedCycle = expected }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REOPEN_JOB_INELIGIBLE", false), result);
        Assert.Empty(admission.SourceReads);
        Assert.Empty(repository.Reopened);
    }

    [Theory]
    [InlineData("missing confirmation")]
    [InlineData("unpinned version")]
    [InlineData("different correlation")]
    [InlineData("source still processing")]
    [InlineData("different media kind")]
    public async Task RequiresCoherentOriginalAndConfirmationEvidenceToReopenAsync(string mismatch)
    {
        var job = SetFailedJob();
        switch (mismatch)
        {
            case "missing confirmation": repository.Job = job with { ConfirmationMessageId = null }; break;
            case "unpinned version": admission.Source = admission.Source! with { VersionId = "null" }; break;
            case "different correlation": repository.Fact = repository.Fact! with { CorrelationId = Guid.NewGuid() }; break;
            case "source still processing": admission.Source = admission.Source! with { ProcessingState = "processing" }; break;
            case "different media kind": admission.Source = admission.Source! with { Kind = "audio" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mismatch));
        }

        var result = await Service.ReopenAsync(ReopenRequest with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REOPEN_SOURCE_UNPROVEN", false), result);
        Assert.Empty(repository.VariantReads);
        Assert.Empty(repository.Reopened);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("image")]
    [InlineData("playback")]
    public async Task LeavesExistingVariantReferencesUntouchedWhenTheFailedSetCannotBeProvedAsync(string? variant)
    {
        SetFailedJob();
        repository.FailedVariants = variant is null ? null : [variant];

        var result = await Service.ReopenAsync(ReopenRequest with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REOPEN_VARIANTS_INELIGIBLE", false), result);
        Assert.Empty(repository.Reopened);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task PermitsMissingWorkOnlyThroughTheInclusiveOrdinaryReplayHorizonAsync(int seconds, bool admitted)
    {
        using var cancellation = new CancellationTokenSource();
        clock.Now = clock.Now.AddSeconds(seconds);

        var result = await Service.RedriveAsync(RedriveRequest with { Apply = true }, cancellation.Token);

        Assert.Equal(new MediaRecoveryResult(admitted ? "MEDIA_REDRIVE_ADMITTED" : "MEDIA_REDRIVE_EFFECT_UNPROVEN", admitted), result);
        Assert.Equal(admitted ? 1 : 0, admission.Jobs.Count);
        Assert.Equal(admitted ? 1 : 0, admission.Deliveries.Count);
        if (admitted)
        {
            Assert.Equal(Confirmation, Assert.Single(admission.Jobs).Message);
            var delivery = Assert.Single(admission.Deliveries);
            Assert.Equal(Confirmation, delivery.Message);
            Assert.Equal(MediaConfirmationCodec.Hash(Confirmation), delivery.Hash);
            Assert.Equal(cancellation.Token, delivery.Token);
        }
        AssertAudit(Assert.Single(audit.Entries), "media.redrive", admitted ? "success" : "rejected", result.Code, null,
            cancellation.Token, Confirmation.MessageId);
        Assert.True(Assert.Single(admission.Transactions).Committed);
    }

    [Fact]
    public async Task PreviewsOrdinaryAdmissionAndAuditsWithoutWritingTheJobOrDeliveryAsync()
    {
        var result = await Service.RedriveAsync(RedriveRequest, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REDRIVE_ADMISSION_PREVIEW", true), result);
        Assert.Empty(admission.Deliveries);
        Assert.Empty(admission.Jobs);
        Assert.Empty(repository.Reopened);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task RestoresProtectionBeyondTheHorizonWithoutChangingTheExistingTerminalEffectAsync(string state)
    {
        using var cancellation = new CancellationTokenSource();
        var prior = SetFailedJob() with { State = state };
        repository.Job = prior;
        clock.Now = Confirmation.ConfirmedAt.AddDays(38);

        var preview = await Service.RedriveAsync(RedriveRequest, cancellation.Token);
        Assert.Equal(new MediaRecoveryResult("MEDIA_REDRIVE_PROTECTION_PREVIEW", true), preview);
        Assert.Empty(admission.Deliveries);

        var applied = await Service.RedriveAsync(RedriveRequest with { Apply = true }, cancellation.Token);
        var delivery = Assert.Single(admission.Deliveries);
        repository.Delivery = new(7, delivery.Hash, "completed");
        var repeated = await Service.RedriveAsync(RedriveRequest with { Apply = true }, cancellation.Token);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REDRIVE_PROTECTION_RESTORED", true), applied);
        Assert.Equal(new MediaRecoveryResult("MEDIA_REDRIVE_ALREADY_PROTECTED", true), repeated);
        Assert.Equal((Confirmation, clock.Now, cancellation.Token), (delivery.Message, delivery.Now, delivery.Token));
        Assert.Equal(MediaConfirmationCodec.Hash(Confirmation), delivery.Hash);
        Assert.Equal(prior, repository.Job);
        Assert.Empty(admission.Jobs);
        Assert.Empty(repository.Reopened);
        Assert.Single(admission.Deliveries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoresProtectionAfterOutboxArchivalUsingTheOriginalReceiptAndPersistedEffectAsync(bool receiptWithCorrelation)
    {
        using var cancellation = new CancellationTokenSource();
        clock.Now = Confirmation.ConfirmedAt.AddDays(61);
        var prior = SetFailedJob();
        repository.Fact = null;
        history.Sources.Add(Confirmation.FileId, admission.Source!);
        history.Receipt = new("photo", "contribution.create", "confirmed", Confirmation.ConfirmedAt, "confirmed",
            Confirmation.ConfirmedAt, receiptWithCorrelation ? Confirmation.CorrelationId : null);
        history.Work = new(prior.Id, prior.ConfirmationMessageId, prior.ConfirmationCorrelationId, prior.ConfirmedAt, false);

        var result = await Service.RedriveAsync(RedriveRequest with { Apply = true }, cancellation.Token);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REDRIVE_PROTECTION_RESTORED", true), result);
        Assert.Equal((Confirmation.EventId, Confirmation.MessageId, cancellation.Token), Assert.Single(history.FactReads));
        var restored = Assert.Single(admission.Deliveries);
        Assert.Equal(Confirmation, restored.Message);
        Assert.Equal(MediaConfirmationCodec.Hash(Confirmation), restored.Hash);
        Assert.Equal(prior, repository.Job);
        Assert.Empty(admission.Jobs);
        Assert.Empty(repository.Reopened);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task NeverTreatsAnArchivedFactAsNewWithoutProvingItsPersistedEffectAsync(int? confirmationOffsetSeconds)
    {
        clock.Now = Confirmation.ConfirmedAt.AddDays(61);
        repository.Fact = null;
        history.Sources.Add(Confirmation.FileId, MediaPreparationTestFixture.Source());
        history.Receipt = new("photo", "contribution.create", "confirmed", Confirmation.ConfirmedAt, "confirmed",
            Confirmation.ConfirmedAt, Confirmation.CorrelationId);
        if (confirmationOffsetSeconds is int seconds)
        {
            history.Work = new(1, Confirmation.MessageId, Confirmation.CorrelationId, Confirmation.ConfirmedAt.AddSeconds(seconds), false);
        }

        var result = await Service.RedriveAsync(RedriveRequest with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult("MEDIA_REDRIVE_FACT_UNPROVEN", false), result);
        Assert.Empty(admission.Deliveries);
        Assert.Empty(admission.Jobs);
        Assert.Empty(repository.Reopened);
        Assert.Equal("rejected", Assert.Single(audit.Entries).Entry.Outcome);
    }

    [Theory]
    [InlineData("wrong event", "MEDIA_REDRIVE_FACT_UNPROVEN")]
    [InlineData("invalid fact", "MEDIA_REDRIVE_FACT_UNPROVEN")]
    [InlineData("unversioned original", "MEDIA_REDRIVE_SOURCE_UNPROVEN")]
    [InlineData("conflicting delivery", "MEDIA_REDRIVE_DELIVERY_CONFLICT")]
    [InlineData("terminal delivery", "MEDIA_REDRIVE_DELIVERY_CONFLICT")]
    [InlineData("changed effect", "MEDIA_REDRIVE_EFFECT_UNPROVEN")]
    [InlineData("delivery without effect", "MEDIA_REDRIVE_EFFECT_UNPROVEN")]
    [InlineData("source already prepared", "MEDIA_REDRIVE_EFFECT_UNPROVEN")]
    public async Task RejectsRedriveWhenConfirmationOrTheExistingEffectCannotBeProvedAsync(string scenario, string code)
    {
        var command = RedriveRequest with { Apply = true };
        switch (scenario)
        {
            case "wrong event": command = command with { EventId = command.EventId + 1 }; break;
            case "invalid fact": repository.Fact = repository.Fact! with { Payload = "{}" }; break;
            case "unversioned original": admission.Source = admission.Source! with { VersionId = "" }; break;
            case "conflicting delivery": repository.Delivery = new(1, new byte[32], "completed"); break;
            case "terminal delivery": repository.Delivery = new(1, MediaConfirmationCodec.Hash(Confirmation), "quarantined"); break;
            case "changed effect": repository.Job = SetFailedJob() with { ConfirmationCorrelationId = Guid.NewGuid() }; break;
            case "delivery without effect": repository.Delivery = new(1, MediaConfirmationCodec.Hash(Confirmation), "completed"); break;
            case "source already prepared": admission.Source = admission.Source! with { ProcessingState = "available" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var result = await Service.RedriveAsync(command, CancellationToken.None);

        Assert.Equal(new MediaRecoveryResult(code, false), result);
        Assert.Equal((command.EventId, Confirmation.MessageId, CancellationToken.None), Assert.Single(repository.FactReads));
        Assert.Empty(admission.Jobs);
        Assert.Empty(admission.Deliveries);
        Assert.Empty(repository.Reopened);
        Assert.Equal("rejected", Assert.Single(audit.Entries).Entry.Outcome);
    }

    [Fact]
    public async Task PropagatesCancellationAndDoesNotCommitAnInterruptedRecoveryAsync()
    {
        using var cancellation = new CancellationTokenSource();
        admission.BeforeCommitAsync = token =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service.RedriveAsync(RedriveRequest with { Apply = true }, cancellation.Token));

        var transaction = Assert.Single(admission.Transactions);
        Assert.True(transaction.Disposed);
        Assert.False(transaction.Committed);
    }

    private MediaRecoveryJobEntity SetFailedJob(string kind = "photo")
    {
        admission.Source = admission.Source! with { Kind = kind, ProcessingState = "failed" };
        repository.FailedVariants = kind == "photo" ? ["image", "thumbnail"] : ["playback"];
        var job = new MediaRecoveryJobEntity(941, Confirmation.RecordId, Confirmation.FileId, 407, 2, "failed", kind,
            Confirmation.MessageId, Confirmation.CorrelationId, Confirmation.ConfirmedAt, false, false, 7);
        repository.Job = job;
        return job;
    }

    private static void AssertAudit((MediaRecoveryAuditModel Entry, CancellationToken Token) item, string action, string outcome,
        string code, int? cycle, CancellationToken token, Guid? messageId = null)
    {
        var entry = item.Entry;
        Assert.Equal(("scrapbook_media_operator", Confirmation.EventId, (long?)Confirmation.FileId, action, outcome),
            (entry.Operator, entry.EventId, entry.FileId, entry.Action, entry.Outcome));
        Assert.Equal((code, Incident, Revision, cycle, messageId, token),
            (entry.ReasonCode, entry.Incident, entry.Revision, entry.Cycle, entry.MessageId, item.Token));
        Assert.NotEqual(Guid.Empty, entry.CorrelationId);
    }
}
