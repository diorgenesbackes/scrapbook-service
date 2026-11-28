using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaHistoryRepository
{
    Task<IReadOnlyList<MediaHistoryCandidateModel>> FindPageAsync(long eventId, long afterId, int limit,
        CancellationToken cancellationToken);
    Task<MediaSourceEntity?> FindSourceAsync(long eventId, long recordId, long fileId, bool lockFile,
        CancellationToken cancellationToken);
    Task<MediaHistoryReceiptModel?> FindReceiptAsync(long eventId, long recordId, CancellationToken cancellationToken);
    Task<MediaHistoryWorkModel> FindWorkAsync(long fileId, string kind, int profileVersion, CancellationToken cancellationToken);
    Task<bool> InsertJobAsync(MediaConfirmationModel confirmation, int profileVersion, string kind, string profile,
        string[] variantKinds, DateTimeOffset now, CancellationToken cancellationToken);
}
