namespace ScrapBook.Domain.Models;

public sealed record PublicEventPresentationModel(string Code, string Name, string EffectiveWelcomeMessage, string CoverAssetKey,
    string? PublicUrl, DateTimeOffset ServerTime, PublicSubmissionWindowModel SubmissionWindow);
