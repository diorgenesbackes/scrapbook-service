using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Interfaces;

public interface IMediaPreparationRepository
{
    Task<IEventTransaction> BeginAsync(CancellationToken cancellationToken);
    Task<MediaJobEntity?> FindEligiblePhotoAsync(DateTimeOffset now, bool historical, CancellationToken cancellationToken);
    Task<MediaSourceEntity?> FindSourceAsync(MediaConfirmationModel message, bool lockFile, CancellationToken cancellationToken);
    Task<long> StartPhotoAsync(MediaJobEntity job, string bucket, Guid lease, DateTimeOffset leaseUntil, Guid requestKey,
        string outputPrefix, int number, string[] variantKinds, DateTimeOffset now, CancellationToken cancellationToken);
    Task EndAttemptAsync(long attemptId, string state, string? errorCode, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RetainPhotoLeaseAsync(PhotoPreparationWorkModel work, DateTimeOffset now, CancellationToken cancellationToken);
    Task PromoteVariantAsync(PhotoPreparationWorkModel work, PreparedPhotoVariantModel variant,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task SetPhotoStateAsync(MediaJobEntity job, string jobState, string fileState, string? variantState, string? errorCode,
        DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken);
    Task InsertFailureAsync(MediaFailureEntity result, CancellationToken cancellationToken);
    Task<MediaFileProjectionModel?> GetFileAsync(long eventId, long recordId, long fileId, CancellationToken cancellationToken);
}
