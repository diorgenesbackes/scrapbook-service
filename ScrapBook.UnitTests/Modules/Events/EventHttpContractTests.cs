using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ScrapBook.Api.Modules.Events;
using ScrapBook.Application.Modules.Events;

namespace ScrapBook.UnitTests.Modules.Events;

public sealed class EventHttpContractTests
{
    private static DefaultHttpContext Context(string body, string contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        return context;
    }

    [Theory]
    [InlineData("{\"name\":\"Festa\",\"startsAt\":\"2027-05-23T18:30:00\",\"accountId\":\"1\"}")]
    [InlineData("{\"name\":\"Festa\",\"startsAt\":\"2027-05-23T18:30:00\",\"version\":\"1\"}")]
    [InlineData("{\"Name\":\"Festa\",\"startsAt\":\"2027-05-23T18:30:00\"}")]
    [InlineData("{\"name\":\"Festa\"}")]
    public async Task CreationRejectsUnknownOrMissingFields(string body)
    {
        var error = await Assert.ThrowsAsync<EventFailure>(() => EventHttpContracts.ReadAsync<CreateEventRequest>(Context(body)));
        Assert.Equal(EventFailureKind.Validation, error.Kind);
    }

    [Theory]
    [InlineData("{\"name\":\"Festa\",\"startsAt\":\"2027-05-23T18:30:00\",\"expectedVersion\":\"0\"}")]
    [InlineData("{\"name\":\"Festa\",\"startsAt\":\"2027-05-23T18:30:00\",\"welcomeMessage\":null,\"expectedVersion\":0}")]
    public async Task UpdateDistinguishesNullFromMissingAndRejectsNumericVersion(string body)
    {
        await Assert.ThrowsAsync<EventFailure>(() => EventHttpContracts.ReadAsync<UpdateEventRequest>(Context(body)));
    }

    [Fact]
    public async Task UpdateAcceptsExplicitNullAndPreservesBigint()
    {
        var result = await EventHttpContracts.ReadAsync<UpdateEventRequest>(Context("""
            {"name":"Festa","startsAt":"2027-05-23T18:30:00","welcomeMessage":null,"expectedVersion":"9223372036854775807"}
            """));
        Assert.Null(result.WelcomeMessage);
        Assert.Equal(long.MaxValue, EventHttpContracts.Decimal(result.ExpectedVersion, "expectedVersion", false));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("1e3")]
    [InlineData(" 1")]
    public void InvalidBigintIsNotCoerced(string value) =>
        Assert.Throws<EventFailure>(() => EventHttpContracts.Decimal(value, "eventId", true));

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json; charset=utf-16")]
    public async Task WrongMediaTypeFailsBeforeReading(string contentType)
    {
        var error = await Assert.ThrowsAsync<EventFailure>(() => EventHttpContracts.ReadAsync<CreateEventRequest>(Context("{}", contentType)));
        Assert.Equal(EventFailureKind.UnsupportedMediaType, error.Kind);
    }

    [Fact]
    public async Task OversizedBodyFailsBeforeReading()
    {
        var context = Context("{}");
        context.Request.ContentLength = EventHttpContracts.MaxBodyBytes + 1;
        var error = await Assert.ThrowsAsync<EventFailure>(() => EventHttpContracts.ReadAsync<CreateEventRequest>(context));
        Assert.Equal(EventFailureKind.TooLarge, error.Kind);
    }

    [Fact]
    public void UnknownErrorHasNoDiagnosticDetails()
    {
        var failure = EventsBoundary.Sanitize(new InvalidOperationException("password=private; SQL PRIVATE"));
        Assert.Equal(EventFailureKind.Unexpected, failure.Kind);
        Assert.Null(failure.Errors);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(failure.Errors));
    }
}
