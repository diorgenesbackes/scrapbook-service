using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaAdmissionRepositoryStub : IMediaAdmissionRepository
{
    public MediaSourceEntity? Source { get; set; } = MediaPreparationTestFixture.Source();
    public ProcessedMessageEntity? Delivery { get; set; }
    public Func<CancellationToken, Task>? BeforeCommitAsync { get; set; }
    public bool JobInserted { get; set; } = true;
    public List<EventWriteTestTransaction> Transactions { get; } = [];
    public List<(MediaConfirmationModel Message, bool Locked, CancellationToken Token)> SourceReads { get; } = [];
    public List<(string Consumer, string Source, Guid MessageId, CancellationToken Token, bool SourceLocked)> DeliveryReads { get; } = [];
    public List<(MediaConfirmationModel Message, int ProfileVersion, string Kind, string[] VariantKinds, DateTimeOffset Now, CancellationToken Token)> Jobs { get; } = [];
    public List<(string Consumer, string Source, MediaConfirmationModel Message, byte[] Hash, DateTimeOffset Now, CancellationToken Token)> Deliveries { get; } = [];

    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = new EventWriteTestTransaction { BeforeCommitAsync = BeforeCommitAsync };
        Transactions.Add(transaction);
        return Task.FromResult<IEventTransaction>(transaction);
    }

    public Task<MediaSourceEntity?> FindSourceAsync(MediaConfirmationModel message, bool lockFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceReads.Add((message, lockFile, cancellationToken));
        return Task.FromResult(Source);
    }

    public Task<ProcessedMessageEntity?> FindDeliveryAsync(string consumer, string source, Guid messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeliveryReads.Add((consumer, source, messageId, cancellationToken, SourceReads.LastOrDefault().Locked));
        return Task.FromResult(Delivery);
    }

    public Task InsertDeliveryAsync(string consumer, string source, MediaConfirmationModel message, byte[] hash,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deliveries.Add((consumer, source, message, hash, now, cancellationToken));
        return Task.CompletedTask;
    }

    public Task<bool> InsertJobAsync(MediaConfirmationModel message, int profileVersion, string kind, string profile,
        string[] variantKinds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Jobs.Add((message, profileVersion, kind, variantKinds, now, cancellationToken));
        return Task.FromResult(JobInserted);
    }
}
