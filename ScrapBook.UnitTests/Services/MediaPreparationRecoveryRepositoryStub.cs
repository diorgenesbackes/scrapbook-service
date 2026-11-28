using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaPreparationRecoveryRepositoryStub : IMediaPreparationRecoveryRepository
{
    public OutboxMessageEntity? Fact { get; set; }
    public ProcessedMessageEntity? Delivery { get; set; }
    public MediaRecoveryJobEntity? Job { get; set; }
    public string[]? FailedVariants { get; set; } = ["image", "thumbnail"];
    public List<(long EventId, Guid MessageId, CancellationToken Token)> FactReads { get; } = [];
    public List<(long EventId, Guid MessageId, string Consumer, string Source, CancellationToken Token)> DeliveryReads { get; } = [];
    public List<(long EventId, long FileId, int ProfileVersion, bool Locked, CancellationToken Token)> JobReads { get; } = [];
    public List<(long EventId, long FileId, int ProfileVersion, CancellationToken Token)> VariantReads { get; } = [];
    public List<(long EventId, MediaRecoveryJobEntity Job, int ProfileVersion, int NextCycle, string[] Variants,
        DateTimeOffset Now, CancellationToken Token)> Reopened
    { get; } = [];

    public Task<OutboxMessageEntity?> FindFactAsync(long eventId, Guid messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FactReads.Add((eventId, messageId, cancellationToken));
        return Task.FromResult(Fact?.EventId == eventId && Fact.MessageId == messageId ? Fact : null);
    }

    public Task<ProcessedMessageEntity?> FindDeliveryAsync(long eventId, Guid messageId, string consumer, string source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeliveryReads.Add((eventId, messageId, consumer, source, cancellationToken));
        return Task.FromResult(Delivery);
    }

    public Task<MediaRecoveryJobEntity?> FindJobAsync(long eventId, long fileId, int profileVersion, bool lockJob,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JobReads.Add((eventId, fileId, profileVersion, lockJob, cancellationToken));
        return Task.FromResult(Job);
    }

    public Task<string[]?> FindFailedVariantsAsync(long eventId, long fileId, int profileVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VariantReads.Add((eventId, fileId, profileVersion, cancellationToken));
        return Task.FromResult(FailedVariants);
    }

    public Task ReopenAsync(long eventId, MediaRecoveryJobEntity job, int profileVersion, int nextCycle, string[] variantKinds,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reopened.Add((eventId, job, profileVersion, nextCycle, variantKinds, now, cancellationToken));
        Job = job with { RetryCycle = nextCycle, State = "pending" };
        return Task.CompletedTask;
    }
}
