using ScrapBook.Domain.Models;
using ScrapBook.Domain.Clocks;
using ScrapBook.Domain.Policies;
using ScrapBook.UnitTests.Providers;
namespace ScrapBook.UnitTests.Models;

public sealed class EventDetailsModelTests
{
    [Fact]
    public void StructuralLockDoesNotRevealItsOriginOrDisablePermittedAccess()
    {
        var clock = new BusinessClock(new ManualTimeProvider(new DateTimeOffset(2027, 5, 23, 15, 0, 0, TimeSpan.Zero)));
        var state = EventPolicy.Create(42, "Festa", new DateTime(2027, 5, 23, 18, 0, 0), null, "immediate", null, clock.CivilNow)
            with
        { LockedAt = new DateTimeOffset(2027, 5, 23, 14, 0, 0, TimeSpan.Zero) };
        var result = EventDetailsModel.Project(state, clock);
        Assert.False(result.CanEditStructure);
        Assert.True(result.CanEditAccess);
        Assert.Equal(EventPolicy.DefaultWelcomeMessage, result.EffectiveWelcomeMessage);
        Assert.Equal("event-cover-default-v1", result.CoverAssetKey);
    }

    [Fact]
    public void CapabilitiesReevaluateTheCurrentClock()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2027, 5, 24, 14, 59, 59, TimeSpan.Zero));
        var clock = new BusinessClock(time);
        var state = EventPolicy.Create(42, "Festa", new DateTime(2027, 5, 23, 18, 0, 0), null, "scheduled", null, clock.CivilNow);
        Assert.True(EventDetailsModel.Project(state, clock).CanEditAccess);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = EventDetailsModel.Project(state, clock);
        Assert.False(result.CanEditAccess);
        Assert.Equal("scheduled_locked", result.AccessState);
    }
}
