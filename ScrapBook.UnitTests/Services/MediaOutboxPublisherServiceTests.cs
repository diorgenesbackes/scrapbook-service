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
    public async Task PublishesTheSameCorrelationAcrossARetryAndUsesTheCurrentLeaseAsync()
    {
        var item = Claimed();
        var recovered = item with { LeaseToken = Guid.NewGuid(), AttemptCount = 2 };
        outbox.CurrentLease = recovered.LeaseToken;

        Assert.Equal(OutboxPublishResult.LeaseLost, await Service.PublishAsync(item, CancellationToken.None));
        Assert.Equal(OutboxPublishResult.Published, await Service.PublishAsync(recovered, CancellationToken.None));

        Assert.Equal(2, publisher.Messages.Count);
        var sent = publisher.Messages[1];
        Assert.Equal("record.media-confirmed.v1", sent.Type);
        Assert.Equal(publisher.Messages[0].Body, sent.Body);
        Assert.Equal(MediaPreparationTestFixture.Confirmation, MediaConfirmationCodec.ReadEnvelope(sent.Body));
        Assert.Equal(recovered.LeaseToken, outbox.Published[1].Lease);
        Assert.Empty(preparation.Failures);
    }

    [Theory]
    [InlineData(1, OutboxPublishResult.RetryScheduled)]
    [InlineData(5, OutboxPublishResult.Failed)]
    public async Task RecordsATransportFailureAndOnlyEmitsTheTerminalFactWhenExhaustedAsync(int attempt, OutboxPublishResult expected)
    {
        var item = Claimed() with { AttemptCount = attempt };
        publisher.Dispatch = _ => throw new OutboxPublishException(true, new InvalidOperationException("provider diagnostic"));

        Assert.Equal(expected, await Service.PublishAsync(item, CancellationToken.None));

        var failure = Assert.Single(outbox.Failures);
        Assert.Equal(item.MessageId, failure.Id);
        Assert.Empty(outbox.Published);
        if (expected == OutboxPublishResult.RetryScheduled)
        {
            Assert.False(failure.Terminal);
            Assert.True(failure.AvailableAt > failure.Now);
            Assert.Empty(preparation.Failures);
        }
        else
        {
            Assert.True(failure.Terminal);
            var fact = Assert.Single(preparation.Failures).Result;
            Assert.Equal("record.media-preparation.failed.v1", fact.Type);
            Assert.Equal((item.MessageId, item.CorrelationId, clock.Now), (fact.CausationId, fact.CorrelationId, fact.OccurredAt));
            Assert.Contains("\"stage\":\"publish\"", fact.Payload);
            Assert.DoesNotContain("provider diagnostic", fact.Payload);
            Assert.True(outbox.Transaction.Committed);
        }
    }

    [Fact]
    public async Task AConfirmationWithoutAnEligibleOriginalCannotBePublishedOrEmitAFailureFactAsync()
    {
        admission.Source = null;

        Assert.Equal(OutboxPublishResult.InvalidFact, await Service.PublishAsync(Claimed(), CancellationToken.None));

        Assert.Empty(publisher.Messages);
        Assert.Empty(preparation.Failures);
        Assert.True(Assert.Single(outbox.Failures).Terminal);
    }

    private static OutboxMessageEntity Claimed() => MediaPreparationTestFixture.Outbox() with
    {
        LeaseToken = Guid.NewGuid(),
        State = "publishing"
    };
}
