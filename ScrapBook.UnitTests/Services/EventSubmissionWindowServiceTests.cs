using System.Globalization;
using ScrapBook.Application.Services;

namespace ScrapBook.UnitTests.Services;

public sealed class EventSubmissionWindowServiceTests
{
    [Theory]
    [InlineData("2031-12-31T02:00:00Z", "before", "2032-01-01T02:00:00Z")]
    [InlineData("2032-01-01T01:59:59.999Z", "before", "2032-01-01T02:00:00Z")]
    [InlineData("2032-01-01T02:00:00Z", "open", "2032-01-02T15:00:00Z")]
    [InlineData("2032-01-02T14:59:59.999Z", "open", "2032-01-02T15:00:00Z")]
    [InlineData("2032-01-02T15:00:00Z", "closed", null)]
    [InlineData("2032-01-02T16:00:00Z", "closed", null)]
    [InlineData("2032-01-02T16:00:00.001Z", "closed", null)]
    public void ClassifiesWindowAcrossYearEnd(string now, string state, string? nextTransition)
    {
        var window = EventSubmissionWindowService.Describe(new DateTime(2031, 12, 31, 23, 0, 0), new DateTime(2032, 1, 2, 12, 0, 0),
            new DateTime(2032, 1, 2, 13, 0, 0), ParseInstant(now));

        Assert.Equal(state, window.State);
        Assert.Equal(nextTransition is null ? (DateTimeOffset?)null : ParseInstant(nextTransition), window.NextTransitionAt);
        Assert.Equal("2031-12-31T23:00:00", window.OpensAt);
        Assert.Equal("2032-01-02T12:00:00", window.ClosesAt);
        Assert.Equal("2032-01-02T13:00:00", window.CompletionDeadlineAt);
    }

    [Fact]
    public void UsesHistoricallyApplicableBrasiliaOffset()
    {
        var window = EventSubmissionWindowService.Describe(new DateTime(2018, 12, 1, 14, 0, 0),
            new DateTime(2018, 12, 2, 12, 0, 0), new DateTime(2018, 12, 2, 13, 0, 0), ParseInstant("2018-12-01T16:00:00Z"));

        Assert.Equal("open", window.State);
        Assert.Equal(ParseInstant("2018-12-02T14:00:00Z"), window.NextTransitionAt);
    }

    private static DateTimeOffset ParseInstant(string input) => DateTimeOffset.Parse(input, CultureInfo.InvariantCulture);
}
