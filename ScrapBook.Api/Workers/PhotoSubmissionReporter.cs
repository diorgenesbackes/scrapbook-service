using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Api.Workers;

public sealed class PhotoSubmissionReporter(ILogger<PhotoSubmissionReporter> logger) : IPhotoSubmissionReporter
{
    public void ReportMissingReceipt(string errorCode, Guid? correlationId) =>
        logger.LogWarning("Media submission is waiting for provider receipt evidence. Error code {ErrorCode}, correlation {CorrelationId}.",
            errorCode, correlationId);
}
