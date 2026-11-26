using System.Text.Json;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class OutboxPublisherServiceTests
{
    private const string FirstConfirmationType = "event.first-record-confirmed.v1";
    private readonly EventTestTimeProvider clock = new();
    private readonly OutboxTestRepository repository = new();
    private readonly OutboxTestPublisher publisher = new();
    private OutboxPublisherService Service => new(repository, publisher, clock);

    [Fact]
    public async Task ClaimsOnlyFirstConfirmationsWithARecoverableLeaseAsync()
    {
        using var cancellation = new CancellationTokenSource();

        await Service.ClaimAsync(cancellation.Token);

        var claim = Assert.Single(repository.Claims);
        Assert.Equal(FirstConfirmationType, claim.Type);
        Assert.InRange(claim.Limit, 1, 10);
        Assert.NotEqual(Guid.Empty, claim.Lease);
        Assert.Equal(clock.Now, claim.Now);
        Assert.Equal(clock.Now.AddSeconds(30), claim.Until);
        Assert.Equal(cancellation.Token, claim.Token);
        Assert.Empty(publisher.Messages);
    }

    [Fact]
    public async Task MarksPublishedOnlyAfterTransportAcceptsAndUsesTheCurrentLeaseAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var accept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Dispatch = _ => accept.Task;
        var item = Message();

        var publishing = Service.PublishAsync(item, cancellation.Token);

        Assert.Empty(repository.Published);
        Assert.Empty(repository.Failures);
        var dispatch = Assert.Single(publisher.Messages);
        Assert.Equal(FirstConfirmationType, dispatch.Type);
        Assert.True(dispatch.Token.CanBeCanceled);
        AssertEnvelope(item, dispatch.Body);

        accept.SetResult();

        Assert.Equal(OutboxPublishResult.Published, await publishing);
        Assert.Equal((item.MessageId, item.LeaseToken!.Value, clock.Now, cancellation.Token),
            Assert.Single(repository.Published));
    }

    [Fact]
    public async Task ReplaysTheSameEnvelopeWhenTheOriginalLeaseIsReplacedAsync()
    {
        var firstLease = Message();
        var recoveredLease = firstLease with { LeaseToken = Guid.NewGuid(), AttemptCount = 2 };
        repository.CurrentLease = recoveredLease.LeaseToken;

        Assert.Equal(OutboxPublishResult.LeaseLost,
            await Service.PublishAsync(firstLease, CancellationToken.None));
        clock.Now = clock.Now.AddSeconds(30);
        Assert.Equal(OutboxPublishResult.Published,
            await Service.PublishAsync(recoveredLease, CancellationToken.None));

        Assert.Collection(repository.Published,
            first => Assert.Equal((firstLease.MessageId, firstLease.LeaseToken!.Value), (first.Id, first.Lease)),
            second => Assert.Equal((firstLease.MessageId, recoveredLease.LeaseToken!.Value), (second.Id, second.Lease)));
        Assert.Equal(2, publisher.Messages.Count);
        Assert.Equal(publisher.Messages[0].Body, publisher.Messages[1].Body);
        AssertEnvelope(firstLease, publisher.Messages[1].Body);
        Assert.Empty(repository.Failures);
    }

    [Theory]
    [InlineData(1, true, false, OutboxPublishResult.RetryScheduled)]
    [InlineData(5, true, true, OutboxPublishResult.Failed)]
    [InlineData(1, false, true, OutboxPublishResult.Failed)]
    public async Task PreservesTheMessageForRetryOrRecoveryAsync(int attempt, bool retryable, bool terminal,
        OutboxPublishResult expected)
    {
        using var cancellation = new CancellationTokenSource();
        var item = Message() with { AttemptCount = attempt };
        publisher.Dispatch = _ => throw new OutboxPublishException(retryable, new InvalidOperationException("provider diagnostic"));

        Assert.Equal(expected, await Service.PublishAsync(item, cancellation.Token));

        var failure = Assert.Single(repository.Failures);
        Assert.Equal((item.MessageId, item.LeaseToken!.Value, terminal, clock.Now, cancellation.Token),
            (failure.Id, failure.Lease, failure.Terminal, failure.Now, failure.Token));
        Assert.False(string.IsNullOrWhiteSpace(failure.ErrorCode));
        Assert.DoesNotContain("provider diagnostic", failure.ErrorCode);
        if (!terminal)
        {
            Assert.Equal(clock.Now.AddSeconds(2), failure.AvailableAt);
        }

        Assert.Empty(repository.Published);
    }

    [Fact]
    public async Task PropagatesCancellationWithoutRecordingAPublicationOutcomeAsync()
    {
        using var cancellation = new CancellationTokenSource();
        publisher.Dispatch = token =>
        {
            cancellation.Cancel();
            Assert.True(token.IsCancellationRequested);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.PublishAsync(Message(), cancellation.Token));

        Assert.Empty(repository.Published);
        Assert.Empty(repository.Failures);
    }

    [Theory]
    [InlineData("record.media-confirmed.v1", 1)]
    [InlineData(FirstConfirmationType, 2)]
    public async Task DoesNotPublishRowsOutsideTheSupportedContractAsync(string type, short version)
    {
        var item = Message() with { Type = type, ProtocolVersion = version };

        Assert.Equal(OutboxPublishResult.Failed, await Service.PublishAsync(item, CancellationToken.None));

        Assert.Empty(publisher.Messages);
        Assert.Empty(repository.Published);
        Assert.True(Assert.Single(repository.Failures).Terminal);
    }

    [Fact]
    public async Task DoesNotDispatchAnUnclaimedRowAsync()
    {
        var item = Message() with { LeaseToken = null };

        Assert.Equal(OutboxPublishResult.LeaseLost, await Service.PublishAsync(item, CancellationToken.None));

        Assert.Empty(publisher.Messages);
        Assert.Empty(repository.Published);
        Assert.Empty(repository.Failures);
    }

    private OutboxMessageEntity Message() => new(
        Guid.Parse("797e003a-e78b-5ade-a746-de13475a04f1"), FirstConfirmationType, 1, "event", 42,
        JsonSerializer.Serialize(new { eventId = "42", recordId = "237", confirmedAt = clock.Now }),
        Guid.Parse("da771942-1b5b-4c4b-885a-62260ad1fc65"), clock.Now, 1, Guid.NewGuid());

    private static void AssertEnvelope(OutboxMessageEntity item, string body)
    {
        using var envelope = JsonDocument.Parse(body);
        var root = envelope.RootElement;
        Assert.Equal(item.MessageId, root.GetProperty("messageId").GetGuid());
        Assert.Equal(FirstConfirmationType, root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("42", root.GetProperty("eventId").GetString());
        Assert.Equal(item.OccurredAt, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(item.CorrelationId, root.GetProperty("correlationId").GetGuid());
        var data = root.GetProperty("data");
        Assert.Equal("237", data.GetProperty("recordId").GetString());
        Assert.Equal(item.OccurredAt, data.GetProperty("confirmedAt").GetDateTimeOffset());
        Assert.False(data.TryGetProperty("eventId", out _));
    }
}
