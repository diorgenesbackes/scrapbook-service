using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaTranscodingProviderStub : IMediaTranscodingProvider
{
    public Func<MediaAudioRequestModel, CancellationToken, Task<MediaProviderJobModel>> Create { get; set; } =
        (request, _) => Task.FromResult(MediaAudioTestFixture.ProviderJob(request.RequestKey));
    public Func<string, CancellationToken, Task<MediaProviderJobModel?>> Get { get; set; } =
        (_, _) => Task.FromResult<MediaProviderJobModel?>(null);
    public Func<MediaAudioRequestModel, DateTimeOffset, CancellationToken, Task<MediaProviderJobModel?>> Find { get; set; } =
        (_, _, _) => Task.FromResult<MediaProviderJobModel?>(null);
    public List<(MediaAudioRequestModel Request, CancellationToken Token)> Submissions { get; } = [];
    public List<(string JobId, CancellationToken Token)> Reads { get; } = [];
    public List<(MediaAudioRequestModel Request, DateTimeOffset SubmittedAt, CancellationToken Token)> Searches { get; } = [];

    public Task<MediaProviderJobModel> CreateAsync(MediaAudioRequestModel request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Submissions.Add((request, cancellationToken));
        return Create(request, cancellationToken);
    }

    public Task<MediaProviderJobModel?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((jobId, cancellationToken));
        return Get(jobId, cancellationToken);
    }

    public Task<MediaProviderJobModel?> FindAsync(MediaAudioRequestModel request, DateTimeOffset submittedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Searches.Add((request, submittedAt, cancellationToken));
        return Find(request, submittedAt, cancellationToken);
    }
}
