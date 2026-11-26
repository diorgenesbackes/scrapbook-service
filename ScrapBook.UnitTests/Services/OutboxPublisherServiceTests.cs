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
    private readonly EventStructuralLockTestRepository locks = new() { ConfirmedAt = new DateTime(2030, 12, 31, 21, 0, 0) };
    private OutboxPublisherService Service => new(repository, publisher, locks, clock);

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
        Assert.Empty(repository.Results);
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

    [Fact]
    public async Task UsesTheStableMessageIdForHistoricalRowsWithoutCorrelationAsync()
    {
        var historical = Message() with { CorrelationId = Guid.Empty };

        Assert.Equal(OutboxPublishResult.Published, await Service.PublishAsync(historical, CancellationToken.None));

        AssertEnvelope(historical with { CorrelationId = historical.MessageId }, Assert.Single(publisher.Messages).Body);
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
        repository.Origin = item;
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
            Assert.Empty(repository.Results);
        }
        else
        {
            var (result, token) = Assert.Single(repository.Results);
            Assert.Equal(cancellation.Token, token);
            StructuralLockOutboxAssertions.Failed(result, item, clock.Now, "publish", failure.ErrorCode);
            Assert.True(repository.Transaction.Committed);
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
        Assert.Empty(repository.Results);
    }

    [Theory]
    [InlineData("record.media-confirmed.v1", 1)]
    [InlineData(FirstConfirmationType, 2)]
    public async Task DoesNotPublishRowsOutsideTheSupportedContractAsync(string type, short version)
    {
        var item = Message() with { Type = type, ProtocolVersion = version };

        Assert.Equal(OutboxPublishResult.InvalidFact, await Service.PublishAsync(item, CancellationToken.None));

        Assert.Empty(publisher.Messages);
        Assert.Empty(repository.Published);
        Assert.True(Assert.Single(repository.Failures).Terminal);
        Assert.Empty(repository.Results);
    }

    [Fact]
    public async Task DoesNotReportAFailedLockWhenTheOriginalConfirmationCannotBeVerifiedAsync()
    {
        var item = Message();
        repository.Origin = item;
        locks.ConfirmedAt = null;

        Assert.Equal(OutboxPublishResult.InvalidFact, await Service.PublishAsync(item, CancellationToken.None));

        Assert.Empty(publisher.Messages);
        Assert.Empty(repository.Results);
        Assert.Empty(repository.Published);
    }

    [Fact]
    public async Task DoesNotReportATerminalFailureForAReplacedLeaseAsync()
    {
        var item = Message() with { AttemptCount = 5 };
        repository.Origin = item with { LeaseToken = Guid.NewGuid() };
        repository.CurrentLease = repository.Origin.LeaseToken;
        publisher.Dispatch = _ => throw new OutboxPublishException(false, new InvalidOperationException());

        Assert.Equal(OutboxPublishResult.LeaseLost, await Service.PublishAsync(item, CancellationToken.None));

        Assert.Empty(repository.Results);
        Assert.Empty(repository.Published);
    }

    [Fact]
    public async Task ReusesTheSameTerminalPublicationOutcomeIdentityAcrossRecoveryAttemptsAsync()
    {
        var item = Message();
        repository.Origin = item;
        publisher.Dispatch = _ => throw new OutboxPublishException(false, new InvalidOperationException());

        Assert.Equal(OutboxPublishResult.Failed, await Service.PublishAsync(item, CancellationToken.None));
        var first = Assert.Single(repository.Results).Message;
        clock.Now = clock.Now.AddSeconds(30);
        var recovered = item with { LeaseToken = Guid.NewGuid(), AttemptCount = 6 };
        repository.Origin = recovered;

        Assert.Equal(OutboxPublishResult.Failed, await Service.PublishAsync(recovered, CancellationToken.None));

        Assert.Equal(2, repository.Results.Count);
        var retry = repository.Results[1].Message;
        Assert.Equal(first.MessageId, retry.MessageId);
        StructuralLockOutboxAssertions.Failed(retry, item, clock.Now, "publish", "PUBLISH_ATTEMPTS_EXHAUSTED");
        Assert.Single(publisher.Messages);
    }

    [Fact]
    public async Task DisposesWithoutCommittingWhenTheTerminalOutcomeCannotBeStoredAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var item = Message() with { AttemptCount = 5 };
        repository.Origin = item;
        publisher.Dispatch = _ => throw new OutboxPublishException(false, new InvalidOperationException());
        repository.BeforeResult = (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.False(repository.Transaction.Committed);
            throw new PostgresUnavailableException();
        };

        await Assert.ThrowsAsync<PostgresUnavailableException>(() => Service.PublishAsync(item, cancellation.Token));

        Assert.False(repository.Transaction.Committed);
        Assert.True(repository.Transaction.Disposed);
        Assert.Empty(repository.Results);
        Assert.Empty(repository.Published);
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
        Guid.Parse("3b0da52a-12ea-1258-ec0b-73e8530e90cf"), FirstConfirmationType, 1, "event", 42,
        JsonSerializer.Serialize(new { eventId = "42", recordId = "237", confirmedAt = clock.Now }),
        Guid.Parse("da771942-1b5b-4c4b-885a-62260ad1fc65"), clock.Now, 1, Guid.NewGuid(), "publishing");

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
