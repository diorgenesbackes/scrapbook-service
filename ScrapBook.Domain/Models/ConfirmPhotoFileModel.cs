using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Models;

public sealed record ConfirmPhotoFileModel(PhotoSubmissionEntity File, PhotoImageModel Image, PhotoObjectModel Original,
    DateTime ReceivedAt);
