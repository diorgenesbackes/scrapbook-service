using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Readers;
using ScrapBook.Api.Requests;
using ScrapBook.Api.Validators;
using ScrapBook.Application.Enums;
using ScrapBook.Application.Exceptions;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ScrapBook.UnitTests.Requests;

public sealed class EventRequestTests
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
        var error = await Assert.ThrowsAsync<EventApplicationException>(() => JsonBodyReader.ReadAsync<CreateEventRequest>(Context(body)));
        Assert.Equal(EventFailureKindEnum.Validation, error.Kind);
    }

    [Theory]
    [InlineData("{\"name\":\"Festa\",\"startsAt\":\"2027-05-23T18:30:00\",\"expectedVersion\":\"0\"}")]
    [InlineData("{\"name\":\"Festa\",\"startsAt\":\"2027-05-23T18:30:00\",\"welcomeMessage\":null,\"expectedVersion\":0}")]
    public async Task UpdateDistinguishesNullFromMissingAndRejectsNumericVersion(string body)
    {
        await Assert.ThrowsAsync<EventApplicationException>(() => JsonBodyReader.ReadAsync<UpdateEventRequest>(Context(body)));
    }

    [Fact]
    public async Task UpdateAcceptsExplicitNullAndPreservesBigint()
    {
        var result = await JsonBodyReader.ReadAsync<UpdateEventRequest>(Context("""
            {"name":"Festa","startsAt":"2027-05-23T18:30:00","welcomeMessage":null,"expectedVersion":"9223372036854775807"}
            """));
        Assert.Null(result.WelcomeMessage);
        Assert.Equal(long.MaxValue, EventRequestValidator.Decimal(result.ExpectedVersion, "expectedVersion", false));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("1e3")]
    [InlineData(" 1")]
    public void InvalidBigintIsNotCoerced(string value) =>
        Assert.Throws<EventApplicationException>(() => EventRequestValidator.Decimal(value, "eventId", true));

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json; charset=utf-16")]
    public async Task WrongMediaTypeFailsBeforeReading(string contentType)
    {
        var error = await Assert.ThrowsAsync<EventApplicationException>(() => JsonBodyReader.ReadAsync<CreateEventRequest>(Context("{}", contentType)));
        Assert.Equal(EventFailureKindEnum.UnsupportedMediaType, error.Kind);
    }

    [Fact]
    public async Task OversizedBodyFailsBeforeReading()
    {
        var context = Context("{}");
        context.Request.ContentLength = EventRequestValidator.MaxBodyBytes + 1;
        var error = await Assert.ThrowsAsync<EventApplicationException>(() => JsonBodyReader.ReadAsync<CreateEventRequest>(context));
        Assert.Equal(EventFailureKindEnum.TooLarge, error.Kind);
    }

    [Fact]
    public void UnknownErrorHasNoDiagnosticDetails()
    {
        var failure = EventRequestMiddleware.Sanitize(new InvalidOperationException("password=private; SQL PRIVATE"));
        Assert.Equal(EventFailureKindEnum.Unexpected, failure.Kind);
        Assert.Null(failure.Errors);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(failure.Errors));
    }
}
