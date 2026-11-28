namespace ScrapBook.Domain.Commands;

public sealed record MediaReopenCommand(long EventId, long FileId, int ProfileVersion, int ExpectedCycle,
    string Incident, string Revision, bool Apply);
