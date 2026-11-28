namespace ScrapBook.Application.Diagnostics;

public enum MediaProcessingStage
{
    Publish,
    PublicationWait,
    Admission,
    ConfirmationConsumption,
    ResultConsumption,
    ClaimWait,
    Preparation,
    Transcoding,
    Ready,
    Failure,
    Reconciliation
}
