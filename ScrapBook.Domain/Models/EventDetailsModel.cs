using ScrapBook.Domain.Clocks;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Policies;
namespace ScrapBook.Domain.Models;

public sealed record EventDetailsModel(EventEntity Configuration, DateTimeOffset ServerTime,
    string AccessState, bool CanEditStructure, bool CanEditAccess)
{
    public string EffectiveWelcomeMessage => EventPolicy.EffectiveWelcomeMessage(Configuration.WelcomeMessage);
    public string CoverAssetKey => EventPolicy.DefaultCoverAssetKey;

    public static EventDetailsModel Project(EventEntity configuration, BusinessClock clock)
    {
        var instant = clock.UtcNow;
        var effective = EventPolicy.Effective(configuration, BusinessClock.ToCivil(instant));
        return new(configuration, instant, effective.AccessState, configuration.LockedAt is null,
            effective.AccessState != "scheduled_locked");
    }
}
