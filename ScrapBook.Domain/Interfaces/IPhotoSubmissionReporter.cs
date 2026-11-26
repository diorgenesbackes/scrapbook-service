namespace ScrapBook.Domain.Interfaces;

public interface IPhotoSubmissionReporter
{
    void ReportMissingReceipt(string errorCode, Guid? correlationId);
}
