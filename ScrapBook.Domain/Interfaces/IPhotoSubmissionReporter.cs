namespace ScrapBook.Domain.Interfaces;

public interface IPhotoSubmissionReporter
{
    void ReportMissingReceipt(Guid? correlationId);
}
