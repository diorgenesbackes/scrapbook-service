namespace ScrapBook.Domain.Entities;

public sealed class PhotoSubmissionEntity
{
    public long SessionId { get; init; }
    public long EventId { get; init; }
    public long RecordId { get; init; }
    public long FileId { get; init; }
    public int Position { get; init; }
    public long TemporaryObjectId { get; init; }
    public long OriginalObjectId { get; init; }
    public required string State { get; init; }
    public required string Bucket { get; init; }
    public required string TemporaryKey { get; init; }
    public required string OriginalKey { get; init; }
    public string? TemporaryVersion { get; init; }
    public DateTimeOffset? ReceiptAt { get; init; }
    public string Format { get; init; } = "photo";
    public string Category { get; init; } = "testimonial";
    public string? MultipartId { get; init; }
    public byte[][]? PartSha256 { get; init; }
    public Guid FileKey { get; init; }
    public required string DeclaredMediaType { get; init; }
    public int DeclaredSize { get; init; }
    public required byte[] Sha256 { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletionDeadlineAt { get; init; }
    public DateTimeOffset? TerminalAt { get; init; }
    public Guid? CorrelationId { get; init; }
}
