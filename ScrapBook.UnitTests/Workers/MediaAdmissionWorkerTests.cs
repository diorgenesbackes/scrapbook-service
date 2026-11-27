using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScrapBook.Api.Workers;
using ScrapBook.Application.Codecs;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Models;
using ScrapBook.UnitTests.Services;

namespace ScrapBook.UnitTests.Workers;

public sealed class MediaAdmissionWorkerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private readonly MediaAdmissionRepositoryStub repository = new();
    private readonly OutboxTestRepository outbox = new() { Origin = MediaPreparationTestFixture.Outbox() };
    private readonly EventTestTimeProvider clock = new() { Now = MediaPreparationTestFixture.Confirmation.ConfirmedAt.AddHours(2) };

    [Fact]
    public async Task AcknowledgesOnlyAfterDurableAdmissionAndLeavesAnInvalidDeliveryUnacknowledgedAsync()
    {
        var enteredCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.BeforeCommitAsync = async token =>
        {
            enteredCommit.SetResult();
            await releaseCommit.Task.WaitAsync(token);
        };
        var envelope = MediaConfirmationCodec.WriteEnvelope(MediaPreparationTestFixture.Confirmation);
        var queue = new MediaConfirmationQueueStub(new MediaConfirmationDelivery(envelope, "valid"), new MediaConfirmationDelivery("{}", "invalid"));
        var acknowledgedAfterCommit = false;
        queue.OnAcknowledgement = () => acknowledgedAfterCommit = Assert.Single(repository.Transactions).Committed;
        await using var services = new ServiceCollection()
            .AddScoped(_ => new MediaAdmissionService(repository, outbox, clock))
            .BuildServiceProvider();
        using var worker = new MediaAdmissionWorker(queue, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MediaAdmissionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await enteredCommit.Task.WaitAsync(WaitLimit);
            Assert.Empty(queue.Acknowledgements);
            releaseCommit.SetResult();
            await queue.Finished.Task.WaitAsync(WaitLimit);

            Assert.Equal("valid", Assert.Single(queue.Acknowledgements));
            Assert.True(acknowledgedAfterCommit);
            Assert.Single(repository.Jobs);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
