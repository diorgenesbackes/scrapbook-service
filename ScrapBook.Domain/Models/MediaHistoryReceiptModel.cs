namespace ScrapBook.Domain.Models;

public sealed record MediaHistoryReceiptModel(string Format, string Operation, string OperationState,
    DateTimeOffset? CompletedAt, string SessionState, DateTimeOffset? TerminalAt, Guid? CorrelationId);
