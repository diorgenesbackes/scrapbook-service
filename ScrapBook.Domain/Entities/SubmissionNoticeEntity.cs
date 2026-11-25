namespace ScrapBook.Domain.Entities;

public sealed class SubmissionNoticeEntity
{
    public long Id { get; init; }
    public required string Code { get; init; }
    public required string Body { get; init; }
}
