namespace ScrapBook.Domain.Commands;

public sealed record MediaRedriveCommand(long EventId, Guid MessageId, string Incident, string Revision, bool Apply);
