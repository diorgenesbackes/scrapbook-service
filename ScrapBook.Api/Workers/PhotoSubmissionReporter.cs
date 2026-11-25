using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Api.Workers;

public sealed class PhotoSubmissionReporter(ILogger<PhotoSubmissionReporter> logger) : IPhotoSubmissionReporter
{
    public void ReportMissingReceipt(Guid? correlationId) =>
        logger.LogWarning("Photo submission is waiting for provider receipt evidence. Error code {ErrorCode}, correlation {CorrelationId}.",
            "photo-receipt-missing", correlationId);
}
