using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Commands;

public sealed record EventStructuralLockRecoveryCommand(EventStructuralLockRecoveryMode Mode, Guid MessageId,
    string Incident, string CandidateSha, bool Rearm = false, bool ReconcileExpired = false, string? Evidence = null);
