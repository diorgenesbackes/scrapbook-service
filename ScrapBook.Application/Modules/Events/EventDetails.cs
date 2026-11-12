using ScrapBook.Domain.Common;
using ScrapBook.Domain.Modules.Events;

namespace ScrapBook.Application.Modules.Events;

public sealed record EventDetails(EventConfiguration Configuration, DateTimeOffset ServerTime,
    string AccessState, bool CanEditStructure, bool CanEditAccess)
{
    public string EffectiveWelcomeMessage => Configuration.WelcomeMessage ?? EventRules.DefaultWelcomeMessage;
    public string CoverAssetKey => EventRules.DefaultCoverAssetKey;

    public static EventDetails Project(EventConfiguration configuration, BusinessClock clock)
    {
        var instant = clock.UtcNow;
        var effective = EventRules.Effective(configuration, BusinessClock.ToCivil(instant));
        return new(configuration, instant, effective.AccessState, configuration.LockedAt is null,
            effective.AccessState != "scheduled_locked");
    }
}
