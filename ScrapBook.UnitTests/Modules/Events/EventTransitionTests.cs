using ScrapBook.Domain.Modules.Events;

namespace ScrapBook.UnitTests.Modules.Events;

public sealed class EventTransitionTests
{
    private static readonly DateTime Start = new(2027, 5, 23, 18, 30, 0);
    private static readonly DateTime Before = new(2027, 5, 24, 11, 59, 59);
    private static readonly DateTime Closed = new(2027, 5, 24, 12, 0, 0);
    private static EventConfiguration State(string mode = "scheduled") =>
        EventRules.Create(42, "Festa", Start, null, mode, null, new DateTime(2027, 1, 1));

    [Fact]
    public void FreeWindowAllowsModeChanges()
    {
        var change = EventRules.UpdateAccess(State(), "immediate", null, Before);
        Assert.True(change.AccessChanged);
        Assert.Equal("flexible", change.State.AccessState);
        Assert.Null(change.State.OpensAt);
    }

    [Theory]
    [InlineData(12, 0, 0)]
    [InlineData(12, 0, 1)]
    [InlineData(12, 59, 59)]
    [InlineData(13, 0, 0)]
    public void ScheduledPolicyCannotChangeAtOrAfterNoon(int hour, int minute, int second)
    {
        var error = Assert.Throws<EventRuleException>(() => EventRules.UpdateAccess(State(), "immediate", null,
            new DateTime(2027, 5, 24, hour, minute, second)));
        Assert.Equal(EventRuleFailure.OpeningLocked, error.Failure);
    }

    [Fact]
    public void ClosedImmediateCanMoveToFutureOnlyOnce()
    {
        var first = EventRules.UpdateAccess(State("immediate"), "scheduled", new DateOnly(2027, 5, 25), Closed);
        Assert.Equal("scheduled_locked", first.State.AccessState);
        Assert.Equal(Closed, first.State.AccessFrozenAt);
        Assert.Throws<EventRuleException>(() => EventRules.UpdateAccess(first.State, "scheduled", new DateOnly(2027, 5, 26), Closed));
        Assert.Throws<EventRuleException>(() => EventRules.UpdateAccess(State("immediate"), "scheduled", new DateOnly(2027, 5, 24), Closed));
    }

    [Fact]
    public void MovingDateAfterOldCloseNeverUnfreezesPolicy()
    {
        var moved = EventRules.UpdateStructure(State("immediate"), "Festa", Start.AddDays(10), null, Closed).State;
        Assert.Equal("immediate_once", moved.AccessState);
        Assert.Equal(Closed, moved.AccessFrozenAt);
        Assert.Equal("immediate_once", EventRules.Effective(moved, Closed.AddMinutes(1)).AccessState);
        Assert.Equal("scheduled_locked", EventRules.UpdateAccess(moved, "scheduled", new DateOnly(2027, 6, 15), Closed).State.AccessState);
    }

    [Fact]
    public void StructuralLockDoesNotBlockIndependentAccessPolicy()
    {
        var locked = State("immediate") with { LockedAt = DateTimeOffset.UtcNow };
        Assert.Equal(EventRuleFailure.StructureLocked,
            Assert.Throws<EventRuleException>(() => EventRules.UpdateStructure(locked, "Outra", Start, null, Before)).Failure);
        Assert.True(EventRules.UpdateAccess(locked, "scheduled", new DateOnly(2028, 5, 25), Before).Changed);
    }

    [Fact]
    public void NewWindowCannotSilentlyInvalidateExistingOpening()
    {
        var original = State();
        Assert.Throws<EventRuleException>(() => EventRules.UpdateStructure(original, "Festa", Start.AddYears(2), null, Before));
        Assert.Equal(Start, original.StartsAt);
    }

    [Fact]
    public void EffectiveNoopDoesNotChangeVersionsButPendingClockTransitionIsAChange()
    {
        var original = State();
        Assert.False(EventRules.UpdateStructure(original, "  Festa  ", Start, "  ", Before).Changed);
        var same = EventRules.UpdateAccess(original, "scheduled", DateOnly.FromDateTime(original.OpensAt!.Value), Before);
        Assert.False(same.Changed);
        Assert.False(same.AccessChanged);
        var frozen = EventRules.UpdateAccess(original, "scheduled", DateOnly.FromDateTime(original.OpensAt.Value), Closed);
        Assert.True(frozen.Changed);
        Assert.False(frozen.AccessChanged);
        Assert.Equal(original.Version, frozen.State.Version);
        Assert.Equal(original.AccessVersion, frozen.State.AccessVersion);
    }

    [Fact]
    public void RetainingOpeningAlreadyInThePastDoesNotPreventRenaming()
    {
        var state = State();
        var change = EventRules.UpdateStructure(state, "Novo nome", Start, null, new DateTime(2029, 1, 1));
        Assert.Equal("Novo nome", change.State.Name);
        Assert.Equal(state.OpensAt, change.State.OpensAt);
    }

    [Fact]
    public void PastEventIsAllowedWhenOpeningIsValid()
    {
        var state = EventRules.Create(42, "Passado", Start, null, "scheduled", new DateOnly(2029, 1, 2), new DateTime(2029, 1, 1));
        Assert.Equal("scheduled_locked", state.AccessState);
    }

    [Fact]
    public void PublicCodeHas256BitBase64UrlFormat()
    {
        Assert.Matches("^[A-Za-z0-9_-]{43}$", EventRules.CreateCode());
    }
}
