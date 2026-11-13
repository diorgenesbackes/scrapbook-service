using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Policies;

namespace ScrapBook.UnitTests.Policies;

public sealed class SubmissionWindowPolicyTests
{
    private static readonly DateTime StartsAt = new(2030, 6, 15, 18, 0, 0);
    private static readonly DateTimeOffset Opening = new(2030, 6, 15, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Closing = new(2030, 6, 16, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, SubmissionWindowStateEnum.NotOpen)]
    [InlineData(0, SubmissionWindowStateEnum.Open)]
    [InlineData(1, SubmissionWindowStateEnum.Open)]
    public void OpeningIsInclusiveAtTickPrecision(long ticks, SubmissionWindowStateEnum expected) =>
        Assert.Equal(expected, SubmissionWindowPolicy.Evaluate(StartsAt, Opening.AddTicks(ticks)).State);

    [Theory]
    [InlineData(-1, SubmissionWindowStateEnum.Open)]
    [InlineData(0, SubmissionWindowStateEnum.Closed)]
    [InlineData(1, SubmissionWindowStateEnum.Closed)]
    public void ClosingIsExclusiveAtTickPrecision(long ticks, SubmissionWindowStateEnum expected) =>
        Assert.Equal(expected, SubmissionWindowPolicy.Evaluate(StartsAt, Closing.AddTicks(ticks)).State);

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(61)]
    public void CompletionToleranceNeverReopensNewSubmissions(int minutes) =>
        Assert.Equal(SubmissionWindowStateEnum.Closed, SubmissionWindowPolicy.Evaluate(StartsAt, Closing.AddMinutes(minutes)).State);

    [Theory]
    [InlineData(2030, 1, 31, 0, 30, 2030, 1, 30, 2030, 2, 1)]
    [InlineData(2030, 12, 31, 23, 59, 2030, 12, 31, 2031, 1, 1)]
    [InlineData(2028, 2, 29, 0, 30, 2028, 2, 28, 2028, 3, 1)]
    public void UsesExistingCivilRuleAcrossCalendarBoundaries(int year, int month, int day, int hour, int minute,
        int openYear, int openMonth, int openDay, int closeYear, int closeMonth, int closeDay)
    {
        var starts = new DateTime(year, month, day, hour, minute, 0);
        var expected = EventPolicy.Window(starts);
        var result = SubmissionWindowPolicy.Evaluate(starts, new DateTimeOffset(starts, TimeSpan.FromHours(-3)));
        Assert.Equal(new DateOnly(openYear, openMonth, openDay), DateOnly.FromDateTime(result.OpensAt));
        Assert.Equal(new DateTime(closeYear, closeMonth, closeDay, 12, 0, 0), result.ClosesAt);
        Assert.Equal(expected.OpensAt, result.OpensAt);
        Assert.Equal(expected.ClosesAt, result.ClosesAt);
        Assert.Equal(SubmissionWindowStateEnum.Open, result.State);
        Assert.Equal(DateTimeKind.Unspecified, result.OpensAt.Kind);
    }

    [Fact]
    public void LeaseIsBoundedAndRoundedDownNearTheBoundary()
    {
        Assert.Equal(30_000, SubmissionWindowPolicy.Evaluate(StartsAt, Opening.AddDays(-30)).RevalidateAfterMs);
        Assert.Equal(1234, SubmissionWindowPolicy.Evaluate(StartsAt, Closing.AddMilliseconds(-1234.9)).RevalidateAfterMs);
        Assert.Equal(0, SubmissionWindowPolicy.Evaluate(StartsAt, Closing.AddTicks(-1)).RevalidateAfterMs);
        Assert.Equal(30_000, SubmissionWindowPolicy.Evaluate(StartsAt, Closing).RevalidateAfterMs);
    }

    [Fact]
    public void EquivalentInstantsWithDifferentOffsetsHaveTheSameState()
    {
        Assert.Equal(SubmissionWindowPolicy.Evaluate(StartsAt, Opening),
            SubmissionWindowPolicy.Evaluate(StartsAt, Opening.ToOffset(TimeSpan.FromHours(9))));
    }

    [Fact]
    public void GalleryAccessCannotChangeSubmissionWindow()
    {
        var scheduled = EventPolicy.Create(1, "Evento", StartsAt, null, "scheduled", new DateOnly(2032, 1, 1), new DateTime(2029, 1, 1));
        var immediate = EventPolicy.UpdateAccess(scheduled, "immediate", null, new DateTime(2029, 1, 1)).State;
        Assert.Equal(SubmissionWindowPolicy.Evaluate(scheduled.StartsAt, Opening), SubmissionWindowPolicy.Evaluate(immediate.StartsAt, Opening));
    }
}
