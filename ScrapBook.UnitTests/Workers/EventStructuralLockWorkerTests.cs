using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScrapBook.Api.Workers;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.UnitTests.Services;
using static ScrapBook.UnitTests.Services.EventStructuralLockTestFixture;

namespace ScrapBook.UnitTests.Workers;

public sealed class EventStructuralLockWorkerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private readonly EventTestTimeProvider clock = new() { Now = ConfirmedAt.AddSeconds(3) };
    private readonly EventWriteTestRepository events = new() { Item = Event() };
    private readonly EventStructuralLockTestRepository locks = new() { ConfirmedAt = ConfirmedInBrasilia };
    private readonly OutboxTestRepository outbox = new() { Origin = Origin() };

    [Fact]
    public async Task AcknowledgesTheDeliveryOnlyAfterTheTransactionCommitsAsync()
    {
        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        events.BeforeCommitAsync = async token =>
        {
            atCommit.SetResult();
            await commitGate.Task.WaitAsync(token);
        };
        var queue = new EventStructuralLockTestQueue(new EventStructuralLockDelivery(Envelope(), "delivery", 1));
        var acknowledgedAfterCommit = false;
        queue.OnAcknowledge = () => acknowledgedAfterCommit = events.Transaction.Committed && events.Transaction.Disposed;
        await using var services = Services();
        using var worker = CreateWorker(queue, services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await atCommit.Task.WaitAsync(WaitLimit);
            Assert.Empty(queue.Acknowledgements);
            Assert.Single(outbox.Results);
            Assert.Single(locks.Completions);
            commitGate.SetResult();
            await queue.PolledAfterDelivery.Task.WaitAsync(WaitLimit);
            Assert.Equal("delivery", Assert.Single(queue.Acknowledgements));
            Assert.True(acknowledgedAfterCommit);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReplaysACommittedDeliveryWhenTheFirstAcknowledgementFailsAsync()
    {
        var queue = new EventStructuralLockTestQueue(
            new EventStructuralLockDelivery(Envelope(), "first", 1),
            new EventStructuralLockDelivery(Envelope(), "redelivery", 2));
        queue.OnAcknowledge = () =>
        {
            if (queue.Acknowledgements.Count == 1)
            {
                throw new EventStructuralLockQueueException(new InvalidOperationException());
            }
        };
        await using var services = Services();
        using var worker = CreateWorker(queue, services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.PolledAfterDelivery.Task.WaitAsync(WaitLimit);
            Assert.Equal(new[] { "first", "redelivery" }, queue.Acknowledgements);
            Assert.Single(locks.Applications);
            Assert.Single(locks.Completions);
            Assert.Single(outbox.Results);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public async Task LeavesAnUncommittedDeliveryForRetryOrDlqWithoutAcknowledgingAsync(int count, bool terminal)
    {
        outbox.BeforeResult = (message, _) =>
        {
            if (message.Type == "event.structural-lock.applied.v1")
            {
                throw new PostgresUnavailableException();
            }
        };
        var queue = new EventStructuralLockTestQueue(new EventStructuralLockDelivery(Envelope(), "delivery", count));
        await using var services = Services();
        using var worker = CreateWorker(queue, services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.PolledAfterDelivery.Task.WaitAsync(WaitLimit);
            Assert.Empty(queue.Acknowledgements);
            Assert.Empty(locks.Completions);
            Assert.True(events.Transaction.Disposed);
            Assert.Equal(terminal, events.Transaction.Committed);
            if (terminal)
            {
                var quarantine = Assert.Single(locks.Quarantines);
                StructuralLockOutboxAssertions.Failed(Assert.Single(outbox.Results).Message,
                    Origin(), clock.Now, "consume", quarantine.ErrorCode);
            }
            else
            {
                Assert.Empty(locks.Quarantines);
                Assert.Empty(outbox.Results);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private ServiceProvider Services() => new ServiceCollection()
        .AddScoped(_ => new EventStructuralLockService(events, new InvitationTestRepository(), locks, outbox, clock))
        .BuildServiceProvider();

    private static EventStructuralLockWorker CreateWorker(EventStructuralLockTestQueue queue, ServiceProvider provider) =>
        new(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EventStructuralLockWorker>.Instance);
}
