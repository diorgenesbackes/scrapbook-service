using ScrapBook.Domain.Modules.Events;

namespace ScrapBook.UnitTests.Modules.Events;

public sealed class EventInputRulesTests
{
    [Theory]
    [InlineData("  Festa  ", "Festa")]
    [InlineData("\uFEFF\u0085Festa\u0085", "Festa")]
    public void NameIsNormalizedConsistently(string input, string expected) => Assert.Equal(expected, EventRules.NormalizeName(input));

    [Fact]
    public void NameLimitsCountUnicodeScalars()
    {
        var valid = string.Concat(Enumerable.Repeat("🎉", 120));
        Assert.Equal(valid, EventRules.NormalizeName(valid));
        Assert.Throws<EventRuleException>(() => EventRules.NormalizeName(valid + "🎉"));
        Assert.Throws<EventRuleException>(() => EventRules.NormalizeName("\uD800"));
        Assert.Throws<EventRuleException>(() => EventRules.NormalizeName("\0"));
        Assert.Throws<EventRuleException>(() => EventRules.NormalizeName("  "));
    }

    [Fact]
    public void WelcomeSupportsDefaultAndUnicodeLimits()
    {
        Assert.Null(EventRules.NormalizeWelcome(null));
        Assert.Null(EventRules.NormalizeWelcome(" \uFEFF\u0085 "));
        Assert.Equal("Olá", EventRules.NormalizeWelcome(" Olá "));
        Assert.NotNull(EventRules.NormalizeWelcome(string.Concat(Enumerable.Repeat("🎉", 2000))));
        Assert.Throws<EventRuleException>(() => EventRules.NormalizeWelcome(new string('a', 2001)));
    }

    [Theory]
    [InlineData("2027-05-23T18:30:00Z")]
    [InlineData("2027-05-23T18:30:00-03:00")]
    [InlineData("2027-02-29T18:30:00")]
    [InlineData("2027-05-23")]
    public void CivilInputRejectsOffsetsAndInvalidFormats(string input) =>
        Assert.Throws<EventRuleException>(() => EventRules.ParseStartsAt(input));

    [Fact]
    public void WindowCrossesMonthAndLeapDayWithoutUtcConversion()
    {
        var start = EventRules.ParseStartsAt("2028-02-29T01:00:00");
        var window = EventRules.Window(start);
        Assert.Equal(new DateTime(2028, 2, 28, 23, 0, 0), window.OpensAt);
        Assert.Equal(new DateTime(2028, 3, 1, 12, 0, 0), window.ClosesAt);
        Assert.Equal(new DateTime(2028, 3, 1, 13, 0, 0), window.DeadlineAt);
        Assert.Equal(new DateOnly(2029, 2, 28), EventRules.SuggestOpening(start));
        Assert.Throws<EventRuleException>(() => EventRules.Window(DateTime.MinValue));
        Assert.Throws<EventRuleException>(() => EventRules.Window(EventRules.ParseStartsAt("9999-12-31T12:00:00")));
    }

    [Fact]
    public void OpeningMustBeAfterTodayAndSubmissionWindow()
    {
        var closes = new DateTime(2027, 5, 24, 12, 0, 0);
        var today = new DateOnly(2027, 5, 23);
        Assert.Null(EventRules.ValidateOpening("immediate", null, closes, today));
        Assert.Throws<EventRuleException>(() => EventRules.ValidateOpening("immediate", today, closes, today));
        Assert.Throws<EventRuleException>(() => EventRules.ValidateOpening("scheduled", new DateOnly(2027, 5, 24), closes, today));
        Assert.Equal(new DateTime(2027, 5, 25), EventRules.ValidateOpening("scheduled", new DateOnly(2027, 5, 25), closes, today));
        Assert.Throws<EventRuleException>(() => EventRules.ValidateOpening("unknown", null, closes, today));
        Assert.Throws<EventRuleException>(() => EventRules.ValidateOpening("scheduled", today, closes, today));
    }

    [Theory]
    [InlineData("2027-05-24T11:59:59", "flexible")]
    [InlineData("2027-05-24T12:00:00", "scheduled_locked")]
    [InlineData("2027-05-24T12:00:01", "scheduled_locked")]
    [InlineData("2027-05-24T12:59:59", "scheduled_locked")]
    [InlineData("2027-05-24T13:00:00", "scheduled_locked")]
    public void NoonFreezesPolicyWithoutCompletionGrace(string now, string expected)
    {
        var state = new EventConfiguration { SubmissionClosesAt = new DateTime(2027, 5, 24, 12, 0, 0) };
        Assert.Equal(expected, EventRules.Effective(state, EventRules.ParseStartsAt(now)).AccessState);
    }
}
