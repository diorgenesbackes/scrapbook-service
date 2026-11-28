using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaRecoveryAccessRepository
{
    Task<string?> FindOperatorAsync(CancellationToken cancellationToken);
    Task AuditAsync(MediaRecoveryAuditModel audit, CancellationToken cancellationToken);
}
