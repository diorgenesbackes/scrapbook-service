using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaRecoveryAccessRepositoryStub : IMediaRecoveryAccessRepository
{
    public string? Operator { get; set; } = "scrapbook_media_operator";
    public List<CancellationToken> OperatorReads { get; } = [];
    public List<(MediaRecoveryAuditModel Entry, CancellationToken Token)> Entries { get; } = [];

    public Task<string?> FindOperatorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OperatorReads.Add(cancellationToken);
        return Task.FromResult(Operator);
    }

    public Task AuditAsync(MediaRecoveryAuditModel audit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entries.Add((audit, cancellationToken));
        return Task.CompletedTask;
    }
}
