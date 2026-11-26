using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class EventStructuralLockTestRepository : IEventStructuralLockRepository
{
    public DateTime? ConfirmedAt { get; set; }
    public byte[]? PriorHash { get; set; }
    public Action? BeforeCompletion { get; set; }
    public List<(long EventId, long RecordId, CancellationToken Token)> RecordLookups { get; } = [];
    public List<(string Consumer, string Source, Guid MessageId, Guid CorrelationId, CancellationToken Token)> Claims { get; } = [];
    public List<(EventStructuralLockCommand Command, CancellationToken Token)> Applications { get; } = [];
    public List<(long Id, DateTimeOffset Time, CancellationToken Token)> Completions { get; } = [];
    private byte[]? payloadHash;

    public Task<DateTime?> FindRecordConfirmationAsync(long eventId, long recordId, CancellationToken cancellationToken)
    {
        RecordLookups.Add((eventId, recordId, cancellationToken));
        return Task.FromResult(ConfirmedAt);
    }

    public Task<ProcessedMessageEntity> GetOrInsertAsync(string consumer, string source, Guid messageId,
        byte[] payloadHash, Guid correlationId, CancellationToken cancellationToken)
    {
        Claims.Add((consumer, source, messageId, correlationId, cancellationToken));
        this.payloadHash ??= PriorHash ?? payloadHash;
        var state = Completions.Count == 0 ? "processing" : "completed";
        return Task.FromResult(new ProcessedMessageEntity(73, this.payloadHash, state));
    }

    public Task CompleteAsync(long id, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        BeforeCompletion?.Invoke();
        Completions.Add((id, completedAt, cancellationToken));
        return Task.CompletedTask;
    }

    public Task ApplyAsync(EventStructuralLockCommand command, CancellationToken cancellationToken)
    {
        Applications.Add((command, cancellationToken));
        return Task.CompletedTask;
    }
}
