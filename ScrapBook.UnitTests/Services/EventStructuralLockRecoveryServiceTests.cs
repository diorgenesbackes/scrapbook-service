using System.Text.Json;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using static ScrapBook.UnitTests.Services.EventStructuralLockTestFixture;

namespace ScrapBook.UnitTests.Services;

public sealed class EventStructuralLockRecoveryServiceTests
{
    private readonly EventTestTimeProvider clock = new() { Now = ConfirmedAt.AddSeconds(3) };
    private readonly EventWriteTestRepository events = new() { Item = Event() };
    private readonly EventStructuralLockTestRepository locks = new() { ConfirmedAt = ConfirmedInBrasilia };
    private readonly OutboxTestRepository outbox = new() { Origin = Origin() };
    private EventStructuralLockRecoveryService Recovery => new(events, locks, outbox, clock);
    private EventStructuralLockService Consumer => new(events, new InvitationTestRepository(), locks, outbox, clock);

    [Fact]
    public async Task ReopensOnlyTheSelectedFailedPublicationAndKeepsItsIdentityAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failed = Origin() with { State = "failed", AttemptCount = 5 };
        outbox.Origin = failed;
        var command = Command(EventStructuralLockRecoveryMode.Publish);

        Assert.Equal(EventStructuralLockRecoveryResult.Rejected,
            await Recovery.RecoverAsync(command with { MessageId = Guid.NewGuid() }, cancellation.Token));
        Assert.Empty(outbox.Reopened);
        Assert.Equal(failed, outbox.Origin);
        Assert.Equal(EventStructuralLockRecoveryResult.Reopened, await Recovery.RecoverAsync(command, cancellation.Token));
        Assert.Equal((MessageId, clock.Now, cancellation.Token), Assert.Single(outbox.Reopened));
        Assert.Equal(failed with { State = "pending", AttemptCount = 0 }, outbox.Origin);
        Assert.True(events.Transaction.Committed);
        var audit = Assert.IsType<AuditEntryEntity>(events.Audit);
        Assert.Equal((42L, "system", "event.structural_lock.recovery_scheduled", CorrelationId),
            (audit.EventId, audit.ActorType, audit.Action, audit.CorrelationId));
        using var evidence = JsonDocument.Parse(audit.AfterState);
        Assert.Equal(command.Incident, evidence.RootElement.GetProperty("incident").GetString());
        Assert.Equal(command.CandidateSha, evidence.RootElement.GetProperty("candidateSha").GetString());
        Assert.Equal(MessageId, evidence.RootElement.GetProperty("messageId").GetGuid());

        Assert.Equal(EventStructuralLockRecoveryResult.AlreadyScheduled, await Recovery.RecoverAsync(command, cancellation.Token));

        Assert.Single(outbox.Reopened);
        Assert.Empty(locks.Rearms);
        Assert.Empty(outbox.Results);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public async Task RecoversAQuarantinedDeliveryPreservingTheTerminalOutcomeAsync(int days, bool reconcileExpired)
    {
        using var cancellation = new CancellationTokenSource();
        var original = Origin();
        var command = Command(EventStructuralLockRecoveryMode.Consume);
        Assert.Equal(EventStructuralLockResult.Terminal,
            await Consumer.RecordTerminalDeliveryAsync(Envelope(), cancellation.Token));
        var failed = Assert.Single(outbox.Results).Message;
        clock.Now = ConfirmedAt.AddDays(days);

        Assert.Equal(EventStructuralLockRecoveryResult.Rejected, await Recovery.RecoverAsync(command, cancellation.Token));
        if (reconcileExpired)
        {
            Assert.Equal(EventStructuralLockRecoveryResult.Rejected,
                await Recovery.RecoverAsync(command with { Rearm = true }, cancellation.Token));
        }
        Assert.Empty(locks.Rearms);
        Assert.Empty(outbox.Reopened);
        Assert.Equal(EventStructuralLockRecoveryResult.Reopened,
            await Recovery.RecoverAsync(command with { Rearm = true, ReconcileExpired = reconcileExpired }, cancellation.Token));

        Assert.Equal((MessageId, clock.Now, cancellation.Token), Assert.Single(outbox.Reopened));
        var rearm = Assert.Single(locks.Rearms);
        Assert.Equal((73L, clock.Now, cancellation.Token), (rearm.Id, rearm.Time, rearm.Token));
        var reopened = Assert.IsType<OutboxMessageEntity>(outbox.Origin);
        Assert.Equal(original with { State = "pending", AttemptCount = 0 }, reopened);
        Assert.Equal(failed, Assert.Single(outbox.Results).Message);
        Assert.True(events.Transaction.Committed);

        outbox.Origin = reopened with { State = "published" };
        Assert.Equal(EventStructuralLockResult.Applied, await Consumer.ConsumeAsync(Envelope(), cancellation.Token));
        Assert.Equal(EventStructuralLockResult.Duplicate, await Consumer.ConsumeAsync(Envelope(), cancellation.Token));

        Assert.Collection(outbox.Results,
            stored => Assert.Equal(failed, stored.Message),
            stored =>
            {
                StructuralLockOutboxAssertions.Applied(stored.Message, original, clock.Now);
                Assert.NotEqual(failed.MessageId, stored.Message.MessageId);
            });
        Assert.Single(locks.Applications);
        Assert.Single(locks.Completions);
        Assert.All(locks.Claims, claim => Assert.Equal((MessageId, CorrelationId), (claim.MessageId, claim.CorrelationId)));
    }

    [Fact]
    public async Task ReconcilesDlqEvidenceWithoutAutomaticallyReopeningTheWorkAsync()
    {
        var command = Command(EventStructuralLockRecoveryMode.ReconcileDlq);

        Assert.Equal(EventStructuralLockRecoveryResult.Rejected,
            await Recovery.RecoverAsync(command with { Evidence = Envelope("238") }, CancellationToken.None));
        Assert.Empty(outbox.Results);
        Assert.Empty(locks.Quarantines);
        Assert.Equal(EventStructuralLockRecoveryResult.Reconciled,
            await Recovery.RecoverAsync(command with { Evidence = Envelope() }, CancellationToken.None));
        Assert.True(events.Transaction.Committed);
        Assert.Equal(EventStructuralLockRecoveryResult.AlreadyTerminal,
            await Recovery.RecoverAsync(command with { Evidence = Envelope() }, CancellationToken.None));

        var quarantine = Assert.Single(locks.Quarantines);
        StructuralLockOutboxAssertions.Failed(Assert.Single(outbox.Results).Message, Origin(), clock.Now, "consume", quarantine.ErrorCode);
        Assert.Empty(outbox.Reopened);
        Assert.Empty(locks.Rearms);
        Assert.Empty(locks.Applications);
        Assert.Empty(locks.Completions);
    }

    [Fact]
    public async Task KeepsPublicationAndConsumptionFailuresDistinctForTheSameConfirmationAsync()
    {
        var original = Origin() with { State = "publishing", LeaseToken = Guid.NewGuid() };
        outbox.Origin = original;
        var publisher = new OutboxTestPublisher
        {
            Dispatch = _ => throw new OutboxPublishException(false, new InvalidOperationException())
        };
        var publication = new OutboxPublisherService(outbox, publisher, locks, clock);

        Assert.Equal(OutboxPublishResult.Failed, await publication.PublishAsync(original, CancellationToken.None));
        var publishedFailure = Assert.Single(outbox.Results).Message;
        outbox.Origin = original with { State = "failed", LeaseToken = null };
        Assert.Equal(EventStructuralLockResult.Terminal,
            await Consumer.RecordTerminalDeliveryAsync(Envelope(), CancellationToken.None));

        Assert.Equal(2, outbox.Results.Count);
        var consumedFailure = outbox.Results[1].Message;
        Assert.NotEqual(publishedFailure.MessageId, consumedFailure.MessageId);
        StructuralLockOutboxAssertions.Failed(publishedFailure, original, clock.Now, "publish", "PUBLISH_TRANSPORT_FAILURE");
        StructuralLockOutboxAssertions.Failed(consumedFailure, original, clock.Now, "consume", "CONSUME_ATTEMPTS_EXHAUSTED");
    }

    [Theory]
    [InlineData(237, EventStructuralLockRecoveryResult.AlreadyCompleted)]
    [InlineData(238, EventStructuralLockRecoveryResult.Rejected)]
    public async Task ReconcilesTheDurableLockBeforeAuthorizingAnExpiredRecoveryAsync(
        long firstRecordId, EventStructuralLockRecoveryResult expected)
    {
        clock.Now = ConfirmedAt.AddDays(31);
        outbox.Origin = Origin() with { State = "failed", AttemptCount = 5 };
        events.Item = Event(ConfirmedAt.AddSeconds(2).UtcDateTime, firstRecordId);
        var command = Command(EventStructuralLockRecoveryMode.Publish) with { ReconcileExpired = true };

        Assert.Equal(expected, await Recovery.RecoverAsync(command, CancellationToken.None));

        Assert.Empty(outbox.Reopened);
        Assert.Empty(outbox.Results);
        Assert.Empty(locks.Applications);
        Assert.Empty(locks.Rearms);
    }

    [Fact]
    public async Task PropagatesCancellationBeforeReopeningTheWorkAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        outbox.Origin = Origin() with { State = "failed", AttemptCount = 5 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Recovery.RecoverAsync(Command(EventStructuralLockRecoveryMode.Publish), cancellation.Token));

        Assert.Empty(outbox.Reopened);
        Assert.Empty(outbox.Results);
        Assert.Empty(locks.Rearms);
    }

    private static EventStructuralLockRecoveryCommand Command(EventStructuralLockRecoveryMode mode) =>
        new(mode, MessageId, "INC-042", "1ee9d816b2339ed4fff79f6d209462f696e03fbe");
}
