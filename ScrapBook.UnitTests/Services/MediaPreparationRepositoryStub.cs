using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaPreparationRepositoryStub : IMediaPreparationRepository
{
    public MediaJobEntity? Eligible { get; set; }
    public MediaSourceEntity? Source { get; set; } = MediaPreparationTestFixture.Source();
    public bool RetainsLease { get; set; } = true;
    public MediaFileProjectionModel? Projection { get; set; }
    public List<EventWriteTestTransaction> Transactions { get; } = [];
    public List<(long EventId, long RecordId, long FileId, CancellationToken Token)> Reads { get; } = [];
    public List<(MediaConfirmationModel Message, bool Locked, CancellationToken Token)> SourceReads { get; } = [];
    public List<(MediaJobEntity Job, string Bucket, Guid Lease, DateTimeOffset Until, Guid RequestKey, string Prefix,
        int Number, string[] Kinds, EventWriteTestTransaction Transaction)> Starts { get; } = [];
    public List<(long Id, string State, string? Code, EventWriteTestTransaction Transaction)> Ends { get; } = [];
    public List<(PhotoPreparationWorkModel Work, DateTimeOffset At, CancellationToken Token)> LeaseChecks { get; } = [];
    public List<(PhotoPreparationWorkModel Work, PreparedPhotoVariantModel Variant, EventWriteTestTransaction Transaction)> Promotions { get; } = [];
    public List<(MediaJobEntity Job, string JobState, string FileState, string? VariantState, string? Code,
        DateTimeOffset? RetryAt, EventWriteTestTransaction Transaction)> States { get; } = [];
    public List<(MediaFailureEntity Result, EventWriteTestTransaction? Transaction)> Failures { get; } = [];

    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = new EventWriteTestTransaction();
        Transactions.Add(transaction);
        return Task.FromResult<IEventTransaction>(transaction);
    }

    public Task<MediaJobEntity?> FindEligiblePhotoAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Eligible);
    }

    public Task<MediaSourceEntity?> FindSourceAsync(MediaConfirmationModel message, bool lockFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceReads.Add((message, lockFile, cancellationToken));
        return Task.FromResult(Source);
    }

    public Task<long> StartPhotoAsync(MediaJobEntity job, string bucket, Guid lease, DateTimeOffset leaseUntil, Guid requestKey,
        string outputPrefix, int number, string[] variantKinds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Starts.Add((job, bucket, lease, leaseUntil, requestKey, outputPrefix, number, variantKinds, Transactions[^1]));
        return Task.FromResult(8L);
    }

    public Task EndAttemptAsync(long attemptId, string state, string? errorCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ends.Add((attemptId, state, errorCode, Transactions[^1]));
        return Task.CompletedTask;
    }

    public Task<bool> RetainPhotoLeaseAsync(PhotoPreparationWorkModel work, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeaseChecks.Add((work, now, cancellationToken));
        return Task.FromResult(RetainsLease);
    }

    public Task PromoteVariantAsync(PhotoPreparationWorkModel work, PreparedPhotoVariantModel variant,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Promotions.Add((work, variant, Transactions[^1]));
        return Task.CompletedTask;
    }

    public Task SetPhotoStateAsync(MediaJobEntity job, string jobState, string fileState, string? variantState, string? errorCode,
        DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        States.Add((job, jobState, fileState, variantState, errorCode, retryAt, Transactions[^1]));
        return Task.CompletedTask;
    }

    public Task InsertFailureAsync(MediaFailureEntity result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Failures.Add((result, Transactions.LastOrDefault()));
        return Task.CompletedTask;
    }

    public Task<MediaFileProjectionModel?> GetFileAsync(long eventId, long recordId, long fileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((eventId, recordId, fileId, cancellationToken));
        return Task.FromResult(Projection);
    }
}
