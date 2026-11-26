using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface IEventStructuralLockRepository
{
    Task<DateTime?> FindRecordConfirmationAsync(long eventId, long recordId, CancellationToken cancellationToken);
    Task<ProcessedMessageEntity> GetOrInsertAsync(string consumer, string source, Guid messageId, byte[] payloadHash,
        Guid correlationId, CancellationToken cancellationToken);
    Task<ProcessedMessageEntity?> FindForUpdateAsync(string consumer, string source, Guid messageId,
        CancellationToken cancellationToken);
    Task QuarantineAsync(long id, string errorCode, DateTimeOffset now, CancellationToken cancellationToken);
    Task RearmAsync(long id, string? errorCode, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> HasUnmatchedConfirmationsAsync(string type, short version, CancellationToken cancellationToken);
    Task CompleteAsync(long id, DateTimeOffset completedAt, CancellationToken cancellationToken);
    Task ApplyAsync(EventStructuralLockCommand command, CancellationToken cancellationToken);
}
