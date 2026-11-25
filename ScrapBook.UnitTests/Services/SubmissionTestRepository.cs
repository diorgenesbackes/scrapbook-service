using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class SubmissionTestRepository : ISubmissionRepository
{
    public static string Code { get; } = new('A', 43);
    public EventEntity Event { get; } = new()
    {
        Id = 5, Code = Code, Name = "Fixture", OpeningMode = "immediate", AccessState = "flexible",
        SubmissionOpensAt = new DateTime(2032, 2, 28, 23, 0, 0),
        SubmissionClosesAt = new DateTime(2032, 3, 1, 12, 0, 0),
        CompletionDeadlineAt = new DateTime(2032, 3, 1, 13, 0, 0)
    };
    public SubmissionNoticeEntity? Notice { get; set; } = new() { Id = 3, Code = "fixture", Body = "Aviso sintético." };
    public EventWriteTestTransaction Transaction { get; } = new();
    public SubmissionAttemptEntity? Attempt { get; set; }
    public ConfirmTextSubmissionCommand? Inserted { get; private set; }
    public SubmissionOutboxEntity? Outbox { get; private set; }
    public bool PriorConfirmation { get; set; }

    public Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken) => Task.FromResult<IEventTransaction>(Transaction);

    public Task<EventEntity?> FindEventAsync(string code, bool forUpdate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<EventEntity?>(code == Event.Code ? Event : null);
    }

    public Task<SubmissionNoticeEntity?> FindNoticeAsync(DateTimeOffset effectiveAt, CancellationToken cancellationToken) =>
        Task.FromResult(Notice);

    public Task<SubmissionAttemptEntity?> FindAttemptAsync(long eventId, string operation, Guid attemptKey,
        CancellationToken cancellationToken) => Task.FromResult(Attempt);

    public Task<bool> HasConfirmedAsync(long eventId, Guid firstMessageId, CancellationToken cancellationToken) =>
        Task.FromResult(PriorConfirmation);

    public Task<long> InsertConfirmedTextAsync(ConfirmTextSubmissionCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Inserted = command;
        return Task.FromResult(19L);
    }

    public Task InsertOutboxAsync(SubmissionOutboxEntity message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Outbox = message;
        return Task.CompletedTask;
    }
}
