using ScrapBook.Domain.Entities;

namespace ScrapBook.Domain.Models;

public sealed record ValidatedSubmissionFileModel(PhotoSubmissionEntity File, string Version, DateTimeOffset ReceivedAt,
    string MediaType, PhotoImageModel? Image, VideoMetadataModel? Video);
