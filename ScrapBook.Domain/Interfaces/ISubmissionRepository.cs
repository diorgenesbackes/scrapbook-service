using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Interfaces;

public interface ISubmissionRepository
{
    Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken);
    Task<EventEntity?> FindEventAsync(string code, bool forUpdate, CancellationToken cancellationToken);
    Task<SubmissionNoticeEntity?> FindNoticeAsync(DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    Task<SubmissionAttemptEntity?> FindAttemptAsync(long eventId, string operation, Guid attemptKey, CancellationToken cancellationToken);
    Task<bool> HasConfirmedAsync(long eventId, Guid firstMessageId, CancellationToken cancellationToken);
    Task<long> InsertConfirmedTextAsync(ConfirmTextSubmissionCommand command, CancellationToken cancellationToken);
    Task InsertOutboxAsync(SubmissionOutboxEntity message, CancellationToken cancellationToken);
}
