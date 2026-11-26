using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class EventStructuralLockTestRepository : IEventStructuralLockRepository
{
    public DateTime? ConfirmedAt { get; set; }
    public byte[]? PriorHash { get; set; }
    public Action? BeforeCompletion { get; set; }
    public ProcessedMessageEntity? Processed { get; set; }
    public List<(long EventId, long RecordId, CancellationToken Token)> RecordLookups { get; } = [];
    public List<(string Consumer, string Source, Guid MessageId, Guid CorrelationId, CancellationToken Token)> Claims { get; } = [];
    public List<(EventStructuralLockCommand Command, CancellationToken Token)> Applications { get; } = [];
    public List<(long Id, DateTimeOffset Time, CancellationToken Token)> Completions { get; } = [];
    public List<(long Id, string ErrorCode, DateTimeOffset Time, CancellationToken Token)> Quarantines { get; } = [];
    public List<(long Id, string? ErrorCode, DateTimeOffset Time, CancellationToken Token)> Rearms { get; } = [];
    public Task<DateTime?> FindRecordConfirmationAsync(long eventId, long recordId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordLookups.Add((eventId, recordId, cancellationToken));
        return Task.FromResult(ConfirmedAt);
    }

    public Task<ProcessedMessageEntity> GetOrInsertAsync(string consumer, string source, Guid messageId,
        byte[] payloadHash, Guid correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Claims.Add((consumer, source, messageId, correlationId, cancellationToken));
        Processed ??= new ProcessedMessageEntity(73, PriorHash ?? payloadHash, "processing");
        return Task.FromResult(Processed);
    }

    public Task<ProcessedMessageEntity?> FindForUpdateAsync(string consumer, string source, Guid messageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Processed);
    }

    public Task QuarantineAsync(long id, string errorCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Quarantines.Add((id, errorCode, now, cancellationToken));
        Processed = Processed! with { State = "quarantined", ErrorCode = errorCode };
        return Task.CompletedTask;
    }

    public Task<bool> HasUnmatchedConfirmationsAsync(string type, short version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task RearmAsync(long id, string? errorCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Rearms.Add((id, errorCode, now, cancellationToken));
        Processed = Processed! with { State = "processing", ErrorCode = errorCode };
        return Task.CompletedTask;
    }

    public Task CompleteAsync(long id, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeCompletion?.Invoke();
        Completions.Add((id, completedAt, cancellationToken));
        Processed = Processed! with { State = "completed" };
        return Task.CompletedTask;
    }

    public Task ApplyAsync(EventStructuralLockCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Applications.Add((command, cancellationToken));
        return Task.CompletedTask;
    }
}
