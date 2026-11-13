using ScrapBook.Domain.Clocks;
using ScrapBook.UnitTests.Providers;
namespace ScrapBook.UnitTests.Clocks;

public sealed class BusinessClockTests
{
    [Fact]
    public void CivilDateUsesBrasiliaInsteadOfUtcDate()
    {
        var clock = new BusinessClock(new ManualTimeProvider(new DateTimeOffset(2026, 11, 13, 1, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 12), clock.Today);
        Assert.Equal(new DateTime(2026, 11, 12, 22, 0, 0), clock.CivilNow);
        Assert.Equal(DateTimeKind.Unspecified, clock.CivilNow.Kind);
    }

    [Fact]
    public void AdvancingClockCrossesTheBusinessDayAtBrasiliaMidnight()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 13, 2, 59, 59, TimeSpan.Zero));
        var clock = new BusinessClock(time);
        Assert.Equal(new DateOnly(2026, 11, 12), clock.Today);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new DateOnly(2026, 11, 13), clock.Today);
        Assert.Equal(0, clock.CivilNow.Hour);
    }
}
