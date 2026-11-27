using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScrapBook.Api.Workers;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.UnitTests.Services;
using static ScrapBook.UnitTests.Services.MediaAudioTestFixture;

namespace ScrapBook.UnitTests.Workers;

public sealed class MediaAudioResultWorkerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AcknowledgesAfterDurableWakeAndKeepsInvalidOrUnavailableDeliveriesForRetryAsync()
    {
        const string unavailable = ProviderJobId + "-unavailable";
        var repository = InFlight(Now.AddSeconds(-30));
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unavailableLookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new MediaTranscodingProviderStub
        {
            Get = async (id, token) =>
            {
                if (id == unavailable)
                {
                    unavailableLookupStarted.TrySetResult();
                    throw new MediaProviderUnavailableException();
                }
                lookupStarted.TrySetResult();
                await releaseLookup.Task.WaitAsync(token);
                return ProviderJob(RequestKey, "complete", OutputKey);
            }
        };
        var wakePersistedWhenAcknowledged = false;
        var queue = new MediaAudioResultQueueStub(new(Envelope(ProviderJobId), "valid"), new("{}", "invalid"),
            new(Envelope(unavailable), "retry"))
        {
            OnAcknowledgement = () => wakePersistedWhenAcknowledged = Assert.Single(repository.Wakes).JobId == ProviderJobId
        };
        var clock = new EventTestTimeProvider { Now = Now };
        await using var services = new ServiceCollection()
            .AddScoped(_ => new MediaAudioPreparationService(repository, new MediaAudioStorageStub(), provider, Settings, clock))
            .BuildServiceProvider();
        using var worker = new MediaAudioResultWorker(queue, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MediaAudioResultWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(lookupStarted.Task, unavailableLookupStarted.Task).WaitAsync(WaitLimit);
            Assert.Empty(queue.Acknowledgements);
            Assert.Empty(repository.Wakes);
            releaseLookup.SetResult();
            await queue.Finished.Task.WaitAsync(WaitLimit);

            Assert.Equal("valid", Assert.Single(queue.Acknowledgements));
            Assert.True(wakePersistedWhenAcknowledged);
            Assert.Empty(repository.Promotions);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static string Envelope(string jobId) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["version"] = "0",
        ["id"] = "9ddd1d0e-a613-4669-8e13-db0bc61a4494",
        ["source"] = "aws.mediaconvert",
        ["detail-type"] = "MediaConvert Job State Change",
        ["account"] = Settings.Account,
        ["region"] = Settings.Region,
        ["resources"] = new[] { $"arn:aws:mediaconvert:{Settings.Region}:{Settings.Account}:jobs/{jobId}" },
        ["detail"] = new
        {
            jobId,
            queue = Settings.QueueArn,
            status = "COMPLETE",
            userMetadata = new { requestKey = RequestKey.ToString("N") }
        }
    });
}
