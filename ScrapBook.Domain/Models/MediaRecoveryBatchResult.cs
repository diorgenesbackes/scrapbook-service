namespace ScrapBook.Domain.Models;

public sealed record MediaRecoveryBatchResult(string Code, long NextId, int Examined, int Changed, int Rejected, int Deferred);
