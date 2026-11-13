using ScrapBook.Application.Services;
using ScrapBook.Domain.Clocks;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Mappers;
using ScrapBook.Domain.Models;
using ScrapBook.UnitTests.Providers;
using ScrapBook.UnitTests.Stubs;

namespace ScrapBook.UnitTests.Services;

public sealed class PublicEventWindowServiceTests
{
    [Fact]
    public async Task ResponseUsesOneServerInstantAndOnlyPublicWindowFields()
    {
        var instant = DateTimeOffset.Parse("2030-06-16T15:00:00Z");
        var clock = new CountingTimeProvider(instant);
        var model = new PublicEventModel(new string('A', 43), "Evento", null, new DateTime(2030, 6, 15, 18, 0, 0));
        var result = await new PublicEventService(new PublicEventRepositoryStub(model), new(null), new BusinessClock(clock)).GetAsync(model.Code, default);
        Assert.Equal(1, clock.Calls);
        Assert.Equal(instant, result.ServerTime);
        Assert.Equal(SubmissionWindowStateEnum.Closed, result.SubmissionWindow.State);
        var response = PublicEventResponseMapper.ToResponse(result);
        Assert.Equal("2030-06-16T15:00:00.000Z", response.ServerTime);
        Assert.Equal("closed", response.SubmissionWindow.State);
        Assert.Equal("2030-06-15T16:00:00", response.SubmissionWindow.OpensAt);
        Assert.Equal("2030-06-16T12:00:00", response.SubmissionWindow.ClosesAt);
        Assert.Equal(BusinessClock.TimeZoneId, response.SubmissionWindow.TimeZone);
    }
}
