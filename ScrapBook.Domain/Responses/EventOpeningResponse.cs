namespace ScrapBook.Domain.Responses;

public sealed record EventOpeningResponse(string Mode, string? OpensOn, string State, string ContentState, bool CanEdit);
