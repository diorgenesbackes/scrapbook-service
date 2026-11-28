using ScrapBook.Application.Codecs;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class MediaOutboxPublisherServiceTests
{
    private readonly OutboxTestRepository outbox = new();
    private readonly OutboxTestPublisher publisher = new();
    private readonly MediaAdmissionRepositoryStub admission = new();
    private readonly MediaPreparationRepositoryStub preparation = new();
    private readonly EventTestTimeProvider clock = new() { Now = MediaPreparationTestFixture.Confirmation.ConfirmedAt.AddSeconds(2) };
    private MediaOutboxPublisherService Service => new(outbox, publisher, admission, preparation, clock);

    [Fact]
    public async Task ClaimsOnlyTheMediaTypeIndependentlyOfTheStructuralLockAsync()
    {
        using var cancellation = new CancellationTokenSource();
        await Service.ClaimAsync(cancellation.Token);

        var claim = Assert.Single(outbox.Claims);
        Assert.Equal("record.media-confirmed.v1", claim.Type);
        Assert.Equal(cancellation.Token, claim.Token);
        Assert.NotEqual(Guid.Empty, claim.Lease);
        Assert.True(claim.Until > clock.Now);
    }

    [Fact]
    public async Task PrioritizesConfirmationsFromTheSharedActivationCutoffAsync()
    {
        var service = new MediaOutboxPublisherService(outbox, publisher, admission, preparation, clock, clock.Now);

        await service.ClaimAsync(CancellationToken.None);

        Assert.Equal(clock.Now, outbox.PrioritizeSince);
    }

    [Fact]
    public async Task PublishesTheSameCorrelationAcrossARetryAndUsesTheCurrentLeaseAsync()
    {
        var item = Claimed();
        var recovered = item with { LeaseToken = Guid.NewGuid(), AttemptCount = 2 };
        outbox.CurrentLease = recovered.LeaseToken;

        Assert.Equal(MediaOutboxPublishResult.LeaseLost, await Service.PublishAsync(item, CancellationToken.None));
        Assert.Equal(MediaOutboxPublishResult.Published, await Service.PublishAsync(recovered, CancellationToken.None));

        Assert.Equal(2, publisher.Messages.Count);
        var sent = publisher.Messages[1];
        Assert.Equal("record.media-confirmed.v1", sent.Type);
        Assert.Equal(publisher.Messages[0].Body, sent.Body);
        Assert.Equal(MediaPreparationTestFixture.Confirmation, MediaConfirmationCodec.ReadEnvelope(sent.Body));
        Assert.Equal(recovered.LeaseToken, outbox.Published[1].Lease);
        Assert.Empty(preparation.Failures);
    }

    [Fact]
    public async Task RetriesAnAmbiguousTransportErrorWithinTheBudgetAsync()
    {
        var item = Claimed();
        publisher.Dispatch = _ => throw new OutboxPublishException(true, new InvalidOperationException("provider diagnostic"));

        Assert.Equal(MediaOutboxPublishResult.RetryScheduled, await Service.PublishAsync(item, CancellationToken.None));

        var failure = Assert.Single(outbox.Failures);
        Assert.Equal(item.MessageId, failure.Id);
        Assert.False(failure.Terminal);
        Assert.True(failure.AvailableAt > failure.Now);
        Assert.Empty(outbox.Published);
        Assert.Empty(preparation.Failures);
        Assert.False(Assert.Single(admission.SourceReads).Locked);
    }

    [Fact]
    public async Task ConfirmsPublicationAfterATransportTimeoutWhenTheScopedDeliveryWasAdmittedAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var item = Claimed() with { AttemptCount = 5 };
        var confirmation = MediaPreparationTestFixture.Confirmation;
        publisher.Dispatch = token =>
        {
            admission.Delivery = CompletedDelivery();
            admission.Source = admission.Source! with { ProcessingState = "processing" };
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        Assert.Equal(MediaOutboxPublishResult.Published, await Service.PublishAsync(item, cancellation.Token));

        Assert.Equal((item.MessageId, item.LeaseToken!.Value, clock.Now, cancellation.Token),
            Assert.Single(outbox.Published));
        Assert.Empty(outbox.Failures);
        Assert.Empty(preparation.Failures);
        Assert.True(outbox.Transaction.Committed);
        Assert.Collection(admission.SourceReads,
            initial => Assert.Equal((confirmation, false, cancellation.Token), initial),
            terminal => Assert.Equal((confirmation, true, cancellation.Token), terminal));
        Assert.Equal(("media-preparation", "internal", item.MessageId, cancellation.Token, true),
            Assert.Single(admission.DeliveryReads));
    }

    [Theory]
    [InlineData(5, true, true)]
    [InlineData(2, false, true)]
    [InlineData(6, false, false)]
    public async Task RequiresReconciliationWhenDeliveryCouldHaveHappenedWithoutProofAsync(int attempt, bool retryable, bool dispatched)
    {
        var item = Claimed() with { AttemptCount = attempt };
        publisher.Dispatch = _ => throw new OutboxPublishException(retryable, new TimeoutException("provider diagnostic"));

        Assert.Equal(MediaOutboxPublishResult.ReconciliationRequired, await Service.PublishAsync(item, CancellationToken.None));

        Assert.Equal(dispatched ? 1 : 0, publisher.Messages.Count);
        AssertReconciliation(item);
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("processing", true)]
    public async Task RequiresReconciliationWhenTheDeliveryHashOrStateCannotProveAdmissionAsync(string state, bool matchingHash)
    {
        var item = Claimed();
        var completed = CompletedDelivery();
        admission.Delivery = completed with { State = state, PayloadHash = matchingHash ? completed.PayloadHash : new byte[32] };
        publisher.Dispatch = _ => throw new OutboxPublishException(false, new InvalidOperationException("known rejection"));

        Assert.Equal(MediaOutboxPublishResult.ReconciliationRequired, await Service.PublishAsync(item, CancellationToken.None));

        AssertReconciliation(item);
    }

    [Fact]
    public async Task EmitsTheTerminalFactForAKnownFirstRejectionWithNoAdmissionAsync()
    {
        var item = Claimed();
        publisher.Dispatch = _ => throw new OutboxPublishException(false, new InvalidOperationException("provider diagnostic"));

        Assert.Equal(MediaOutboxPublishResult.Failed, await Service.PublishAsync(item, CancellationToken.None));

        var failure = Assert.Single(outbox.Failures);
        Assert.True(failure.Terminal);
        Assert.Equal(item.MessageId, failure.Id);
        var fact = Assert.Single(preparation.Failures).Result;
        Assert.Equal("record.media-preparation.failed.v1", fact.Type);
        Assert.Equal((item.MessageId, item.CorrelationId, clock.Now), (fact.CausationId, fact.CorrelationId, fact.OccurredAt));
        Assert.Contains("\"stage\":\"publish\"", fact.Payload);
        Assert.DoesNotContain("provider diagnostic", fact.Payload);
        Assert.True(outbox.Transaction.Committed);
        Assert.Empty(outbox.Published);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AStaleLeaseCannotCommitReconciliationOrAnAdmissionConfirmationAsync(bool admitted, bool expires)
    {
        var item = Claimed() with { AttemptCount = 5 };
        outbox.CurrentLease = item.LeaseToken;
        var leaseExpiresAt = clock.Now.AddSeconds(1);
        outbox.CurrentLeaseExpiresAt = leaseExpiresAt;
        publisher.Dispatch = _ =>
        {
            if (expires) clock.Now = leaseExpiresAt;
            else outbox.CurrentLease = Guid.NewGuid();
            if (admitted) admission.Delivery = CompletedDelivery();
            throw new OutboxPublishException(true, new InvalidOperationException("provider diagnostic"));
        };

        Assert.Equal(MediaOutboxPublishResult.LeaseLost, await Service.PublishAsync(item, CancellationToken.None));

        Assert.False(outbox.Transaction.Committed);
        Assert.True(outbox.Transaction.Disposed);
        Assert.Empty(preparation.Failures);
    }

    [Fact]
    public async Task AConfirmationWithoutAnEligibleOriginalCannotBePublishedOrEmitAFailureFactAsync()
    {
        admission.Source = null;

        Assert.Equal(MediaOutboxPublishResult.InvalidFact, await Service.PublishAsync(Claimed(), CancellationToken.None));

        Assert.Empty(publisher.Messages);
        Assert.Empty(preparation.Failures);
        Assert.True(Assert.Single(outbox.Failures).Terminal);
    }

    private void AssertReconciliation(OutboxMessageEntity item)
    {
        var failure = Assert.Single(outbox.Failures);
        Assert.Equal(item.MessageId, failure.Id);
        Assert.Equal("MEDIA_PUBLISH_RECONCILIATION_REQUIRED", failure.ErrorCode);
        Assert.True(failure.Terminal);
        Assert.True(outbox.Transaction.Committed);
        Assert.Empty(outbox.Published);
        Assert.Empty(preparation.Failures);
    }

    private static ProcessedMessageEntity CompletedDelivery() =>
        new(1, MediaConfirmationCodec.Hash(MediaPreparationTestFixture.Confirmation), "completed");

    private static OutboxMessageEntity Claimed() => MediaPreparationTestFixture.Outbox() with
    {
        LeaseToken = Guid.NewGuid(),
        State = "publishing"
    };
}
