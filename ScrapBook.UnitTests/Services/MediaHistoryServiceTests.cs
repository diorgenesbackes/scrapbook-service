using ScrapBook.Application.Codecs;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class MediaHistoryServiceTests
{
    private const string Revision = "cfe74b654125ebfc8fdf77f2d47c18702f26788b";
    private static readonly MediaConfirmationModel Confirmation = MediaPreparationTestFixture.Confirmation;
    private static readonly MediaRecoveryBatchCommand Command = new(Confirmation.EventId, Confirmation.FileId - 1, 1, "INC-708", Revision, false);
    private readonly MediaHistoryRepositoryStub repository = new();
    private readonly MediaAdmissionRepositoryStub admission = new();
    private readonly OutboxTestRepository outbox = new() { Origin = MediaPreparationTestFixture.Outbox() };
    private readonly MediaRecoveryAccessRepositoryStub audit = new();
    private readonly EventTestTimeProvider clock = new() { Now = Confirmation.ConfirmedAt.AddDays(60) };
    private MediaHistoryService Service => new(repository, admission, outbox, audit, new(audit), clock);

    public MediaHistoryServiceTests() => repository.Sources.Add(Confirmation.FileId, MediaPreparationTestFixture.Source());

    [Fact]
    public async Task UsesAStableEventScopedCursorAndDefaultsToNoMutationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var source = repository.Sources[Confirmation.FileId];
        var next = source with { RecordId = source.RecordId + 1, FileId = source.FileId + 2, Kind = "video" };
        repository.Sources.Add(source.FileId + 1, source with { FileId = source.FileId + 1, EventId = source.EventId + 1 });
        repository.Sources.Add(next.FileId, next);
        repository.Receipt = Receipt("video");

        var page = await Service.ScanAsync(Command, cancellation.Token);
        var second = await Service.ScanAsync(Command with { AfterId = page.NextId }, cancellation.Token);
        var exhausted = await Service.ScanAsync(Command with { AfterId = second.NextId }, cancellation.Token);

        Assert.Equal(new MediaRecoveryBatchResult("OK", source.FileId, 1, 0, 0, 1), page);
        Assert.Equal(new MediaRecoveryBatchResult("OK", next.FileId, 1, 0, 0, 1), second);
        Assert.Equal(new MediaRecoveryBatchResult("OK", next.FileId, 0, 0, 0, 0), exhausted);
        Assert.Equal([(Command.EventId, Command.AfterId, 1, cancellation.Token),
            (Command.EventId, source.FileId, 1, cancellation.Token), (Command.EventId, next.FileId, 1, cancellation.Token)], repository.Pages);
        Assert.Equal([(source.EventId, source.RecordId, source.FileId, false, cancellation.Token),
            (next.EventId, next.RecordId, next.FileId, false, cancellation.Token)], repository.SourceReads);
        Assert.Empty(admission.Transactions);
        Assert.Empty(repository.Jobs);
        Assert.Empty(audit.Entries);
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("receipt")]
    [InlineData("receipt with derived correlation")]
    public async Task RecordsVerifiedHistoryAndProvenanceAtomicallyEvenAfterTheOrdinaryReplayHorizonAsync(string evidence)
    {
        using var cancellation = new CancellationTokenSource();
        if (evidence != "outbox")
        {
            outbox.Origin = null;
            repository.Receipt = Receipt("photo");
        }
        if (evidence == "receipt with derived correlation")
        {
            repository.Receipt = repository.Receipt! with { CorrelationId = null };
        }
        admission.BeforeCommitAsync = token =>
        {
            Assert.Single(repository.Jobs);
            Assert.Single(admission.Deliveries);
            Assert.Single(audit.Entries);
            Assert.Equal(cancellation.Token, token);
            return Task.CompletedTask;
        };

        var result = await Service.ScanAsync(Command with { Apply = true }, cancellation.Token);

        Assert.Equal(new MediaRecoveryBatchResult("OK", Confirmation.FileId, 1, 1, 0, 0), result);
        var job = Assert.Single(repository.Jobs);
        var fact = job.Confirmation;
        Assert.Equal((Confirmation.MessageId, Confirmation.EventId, Confirmation.RecordId, Confirmation.FileId,
            Confirmation.ConfirmedAt, Confirmation.OccurredAt),
            (fact.MessageId, fact.EventId, fact.RecordId, fact.FileId, fact.ConfirmedAt, fact.OccurredAt));
        if (evidence == "receipt with derived correlation") Assert.NotEqual(Guid.Empty, fact.CorrelationId);
        else Assert.Equal(Confirmation.CorrelationId, fact.CorrelationId);
        Assert.Equal((1, "photo_prepare", clock.Now, cancellation.Token), (job.ProfileVersion, job.Kind, job.Now, job.Token));
        Assert.Equal(["image", "thumbnail"], job.Variants);
        Assert.Equal((Confirmation.EventId, Confirmation.RecordId, Confirmation.FileId, true, cancellation.Token),
            Assert.Single(repository.SourceReads));
        var delivery = Assert.Single(admission.Deliveries);
        Assert.Equal(("media-preparation", "internal", fact, clock.Now, cancellation.Token),
            (delivery.Consumer, delivery.Source, delivery.Message, delivery.Now, delivery.Token));
        Assert.Equal(MediaConfirmationCodec.Hash(fact), delivery.Hash);
        var reason = evidence switch
        {
            "outbox" => "MEDIA_HISTORY_OUTBOX",
            "receipt" => "MEDIA_HISTORY_RECEIPT",
            _ => "MEDIA_HISTORY_RECEIPT_TECHNICAL_CORRELATION"
        };
        AssertAudit(Assert.Single(audit.Entries), fact.CorrelationId, "success", reason, cancellation.Token);
        var transaction = Assert.Single(admission.Transactions);
        Assert.True(transaction.Committed);
        Assert.True(transaction.Disposed);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("video")]
    public async Task AppliesTheActivePlaybackProfileForHistoricalAudioAndVideoAsync(string kind)
    {
        repository.Sources[Confirmation.FileId] = repository.Sources[Confirmation.FileId] with { Kind = kind };

        var result = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(1, result.Changed);
        var job = Assert.Single(repository.Jobs);
        Assert.Equal(("av_transcode", 1, "playback"), (job.Kind, job.ProfileVersion, Assert.Single(job.Variants)));
    }

    [Fact]
    public async Task RepeatedRecoveryKeepsTheProvenJobAndCanonicalDeduplicationIntactAsync()
    {
        outbox.Origin = null;
        repository.Receipt = Receipt("photo") with { CorrelationId = null };
        var first = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);
        var confirmed = Assert.Single(repository.Jobs).Confirmation;
        var delivery = Assert.Single(admission.Deliveries);
        repository.Work = new(3, confirmed.MessageId, confirmed.CorrelationId, confirmed.ConfirmedAt, true);
        admission.Delivery = new(5, delivery.Hash, "completed");

        var repeated = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(1, first.Changed);
        Assert.Equal(new MediaRecoveryBatchResult("OK", Confirmation.FileId, 1, 0, 0, 0), repeated);
        Assert.Single(repository.Jobs);
        Assert.Single(admission.Deliveries);
        Assert.Single(audit.Entries);
    }

    [Theory]
    [InlineData("outbox conflict", "MEDIA_HISTORY_OUTBOX_CONFLICT")]
    [InlineData("wrong event outbox", "MEDIA_HISTORY_OUTBOX_CONFLICT")]
    [InlineData("missing receipt", "MEDIA_HISTORY_RECEIPT_UNVERIFIED")]
    [InlineData("wrong operation", "MEDIA_HISTORY_RECEIPT_UNVERIFIED")]
    [InlineData("interrupted session", "MEDIA_HISTORY_RECEIPT_UNVERIFIED")]
    [InlineData("different receipt instant", "MEDIA_HISTORY_RECEIPT_UNVERIFIED")]
    [InlineData("different format", "MEDIA_HISTORY_RECEIPT_UNVERIFIED")]
    [InlineData("empty correlation", "MEDIA_HISTORY_RECEIPT_UNVERIFIED")]
    [InlineData("unpinned version", "MEDIA_HISTORY_SOURCE_UNVERIFIED")]
    [InlineData("checksum mismatch", "MEDIA_HISTORY_SOURCE_UNVERIFIED")]
    [InlineData("different original instant", "MEDIA_HISTORY_SOURCE_UNVERIFIED")]
    [InlineData("legacy work", "MEDIA_HISTORY_LEGACY_JOB_UNVERIFIED")]
    [InlineData("job missing deduplication", "MEDIA_HISTORY_JOB_IDENTITY_CONFLICT")]
    [InlineData("job mismatched hash", "MEDIA_HISTORY_JOB_IDENTITY_CONFLICT")]
    [InlineData("delivery without job", "MEDIA_HISTORY_WORK_CONFLICT")]
    [InlineData("variants without job", "MEDIA_HISTORY_WORK_CONFLICT")]
    [InlineData("source state conflict", "MEDIA_HISTORY_STATE_CONFLICT")]
    public async Task AuditsInconclusiveHistoryWithoutCreatingNewWorkOrDeduplicationAsync(string scenario, string code)
    {
        var source = repository.Sources[Confirmation.FileId];
        var storedReceipt = Receipt("photo");
        switch (scenario)
        {
            case "outbox conflict":
                outbox.Origin = outbox.Origin! with { Payload = "{}" };
                repository.Receipt = storedReceipt;
                break;
            case "wrong event outbox": outbox.Origin = outbox.Origin! with { EventId = Confirmation.EventId + 1 }; break;
            case "missing receipt": outbox.Origin = null; break;
            case "wrong operation": SetReceipt(storedReceipt with { Operation = "other" }); break;
            case "interrupted session": SetReceipt(storedReceipt with { SessionState = "pending" }); break;
            case "different receipt instant": SetReceipt(storedReceipt with { TerminalAt = Confirmation.ConfirmedAt.AddSeconds(1) }); break;
            case "different format": SetReceipt(storedReceipt with { Format = "video" }); break;
            case "empty correlation": SetReceipt(storedReceipt with { CorrelationId = Guid.Empty }); break;
            case "unpinned version": repository.Sources[source.FileId] = source with { VersionId = "null" }; break;
            case "checksum mismatch": repository.Sources[source.FileId] = source with { OriginalSha256 = new byte[32] }; break;
            case "different original instant": repository.Sources[source.FileId] = source with { FileConfirmedAt = source.FileConfirmedAt?.AddSeconds(1) }; break;
            case "legacy work": repository.Work = repository.Work with { JobId = 3 }; break;
            case "job missing deduplication": repository.Work = EstablishedWork(); break;
            case "job mismatched hash":
                repository.Work = EstablishedWork();
                admission.Delivery = new(5, new byte[32], "completed");
                break;
            case "delivery without job": admission.Delivery = new(5, MediaConfirmationCodec.Hash(Confirmation), "completed"); break;
            case "variants without job": repository.Work = repository.Work with { HasVariants = true }; break;
            case "source state conflict": repository.Sources[source.FileId] = source with { ProcessingState = "available" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var result = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", Confirmation.FileId, 1, 0, 1, 0), result);
        Assert.Empty(repository.Jobs);
        Assert.Empty(admission.Deliveries);
        var entry = Assert.Single(audit.Entries);
        Assert.NotEqual(Guid.Empty, entry.Entry.CorrelationId);
        AssertAudit(entry, entry.Entry.CorrelationId, "rejected", code, CancellationToken.None);
        Assert.True(Assert.Single(admission.Transactions).Committed);
        if (scenario == "outbox conflict") Assert.Empty(repository.ReceiptReads);
    }

    [Fact]
    public async Task AuditsAConcurrentInsertConflictWithoutRecordingDeliveryAsync()
    {
        repository.Inserted = false;

        var result = await Service.ScanAsync(Command with { Apply = true }, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult("OK", Confirmation.FileId, 1, 0, 1, 0), result);
        Assert.Single(repository.Jobs);
        Assert.Empty(admission.Deliveries);
        AssertAudit(Assert.Single(audit.Entries), Confirmation.CorrelationId, "rejected", "MEDIA_HISTORY_WORK_CONFLICT", CancellationToken.None);
    }

    [Theory]
    [InlineData(true, "MEDIA_RECOVERY_INVALID_ARGUMENT")]
    [InlineData(false, "MEDIA_RECOVERY_FORBIDDEN")]
    public async Task RejectsInvalidOrUnauthorizedScansBeforeReadingAnyFileAsync(bool invalid, string code)
    {
        audit.Operator = null;
        var command = Command with { Apply = true, AfterId = invalid ? -1 : Command.AfterId };

        var result = await Service.ScanAsync(command, CancellationToken.None);

        Assert.Equal(new MediaRecoveryBatchResult(code, command.AfterId, 0, 0, 0, 0), result);
        Assert.Equal(invalid ? 0 : 1, audit.OperatorReads.Count);
        Assert.Empty(repository.Pages);
        Assert.Empty(admission.Transactions);
        Assert.Empty(audit.Entries);
    }

    private void SetReceipt(MediaHistoryReceiptModel receipt)
    {
        outbox.Origin = null;
        repository.Receipt = receipt;
    }

    private static MediaHistoryReceiptModel Receipt(string kind) => new(kind, "contribution.create", "confirmed",
        Confirmation.ConfirmedAt, "confirmed", Confirmation.ConfirmedAt, Confirmation.CorrelationId);

    private static MediaHistoryWorkModel EstablishedWork() => new(3, Confirmation.MessageId, Confirmation.CorrelationId,
        Confirmation.ConfirmedAt, false);

    private static void AssertAudit((MediaRecoveryAuditModel Entry, CancellationToken Token) item, Guid correlation,
        string outcome, string reason, CancellationToken token)
    {
        var entry = item.Entry;
        Assert.Equal(("scrapbook_media_operator", Confirmation.EventId, (long?)Confirmation.FileId, "media.history", outcome, reason),
            (entry.Operator, entry.EventId, entry.FileId, entry.Action, entry.Outcome, entry.ReasonCode));
        Assert.Equal((Command.Incident, Command.Revision, correlation, token),
            (entry.Incident, entry.Revision, entry.CorrelationId, item.Token));
    }
}
