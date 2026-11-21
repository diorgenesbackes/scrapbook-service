using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Requests;
using Xunit;

namespace ScrapBook.UnitTests.Services;

public sealed class EventServiceTests
{
    private readonly EventTestRepository repository = new();
    private readonly EventTestTimeProvider clock = new();
    private static readonly Guid Key = Guid.Parse("a3ba430d-78ba-4d3c-8f2a-7034b274606f");
    private static readonly CreateEventRequest Request = new(" Celebração ", "2032-02-29T01:30:00", null);
    private EventService Service => new(repository, new EventTestCursorCodec(), clock);

    [Fact]
    public async Task CreatesEventAndFirstMembership()
    {
        var result = await Service.CreateAsync(9, Key.ToString(), Request, CancellationToken.None);
        Assert.False(result.IsReplay);
        Assert.Equal("Celebração", result.Event.Name);
        Assert.Equal("2033-02-28", result.Event.OpeningPolicy.OpensOn);
        Assert.Equal("2032-02-28T23:30:00", result.Event.SubmissionOpensAt);
        Assert.Equal("2032-03-01T12:00:00", result.Event.SubmissionClosesAt);
        Assert.Equal("2032-03-01T13:00:00", result.Event.CompletionDeadlineAt);
        Assert.Equal("default", result.Event.Cover.Kind);
        Assert.Equal(43, repository.Event!.Code.Length);
        Assert.Equal((9L, (short)1), (repository.Member!.AccountId, repository.Member.Slot));
    }

    [Fact]
    public async Task ReplaysTheCanonicalAttemptAfterTimeHasAdvanced()
    {
        var token = CancellationToken.None;
        var first = await Service.CreateAsync(9, Key.ToString(), Request, token);
        clock.Now = DateTimeOffset.Parse("2033-03-04T00:00:00Z");
        var equivalent = Request with { Name = "Celebração", OpeningPolicy = new("scheduled", "2033-02-28") };
        var replay = await Service.CreateAsync(9, Key.ToString(), equivalent, token);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Event.Id, replay.Event.Id);
        Assert.Equal("open", replay.Event.OpeningPolicy.ContentState);
        Assert.Equal("scheduled_locked", replay.Event.OpeningPolicy.State);
        Assert.Equal(1, repository.Inserts);
        await Assert.ThrowsAsync<EventCreationKeyReusedException>(
            () => Service.CreateAsync(9, Key.ToString(), Request with { Name = "Outra festa" }, token));

        repository.Member = null;
        await Assert.ThrowsAsync<EventNotFoundException>(() => Service.CreateAsync(9, Key.ToString(), Request, token));
    }

    [Theory]
    [InlineData("", "2032-02-29T01:30:00", null, null, "name")]
    [InlineData("Nome", "2032-02-29T01:30:00-03:00", null, null, "startsAt")]
    [InlineData("Nome", "2029-02-29T01:30:00", null, null, "startsAt")]
    [InlineData("Nome", "2032-02-29T01:30:00", "other", null, "openingPolicy.mode")]
    [InlineData("Nome", "2032-02-29T01:30:00", "immediate", "2033-02-28", "openingPolicy.opensOn")]
    [InlineData("Nome", "2032-02-29T01:30:00", "scheduled", "2032-03-01", "openingPolicy.opensOn")]
    [InlineData("Nome", "2032-02-29T01:30:00", "scheduled", "2033-02-30", "openingPolicy.opensOn")]
    public async Task RefusesInvalidCreation(string name, string startsAt, string? mode, string? date, string field)
    {
        var request = new CreateEventRequest(name, startsAt, mode is null ? null : new(mode, date));
        var error = await Assert.ThrowsAsync<EventRequestValidationException>(() =>
            Service.CreateAsync(9, Key.ToString(), request, CancellationToken.None));
        Assert.Equal(field, error.Field);
        Assert.Equal(0, repository.Inserts);
    }

    [Fact]
    public async Task CountsPostgresCharactersAndRejectsNonVersionFourKeys()
    {
        var token = CancellationToken.None;
        var tooLong = Request with { Name = new string('a', 121) };
        Assert.Equal("name", (await Assert.ThrowsAsync<EventRequestValidationException>(
            () => Service.CreateAsync(9, Key.ToString(), tooLong, token))).Field);
        Assert.Equal("Idempotency-Key", (await Assert.ThrowsAsync<EventRequestValidationException>(
            () => Service.CreateAsync(9, Guid.Empty.ToString(), Request, token))).Field);
        var emoji = Request with { Name = string.Concat(Enumerable.Repeat("🎉", 120)) };
        Assert.Equal(emoji.Name, (await Service.CreateAsync(9, Key.ToString(), emoji, token)).Event.Name);
    }

    [Fact]
    public async Task CreatesImmediateOpeningAndKeepsItsReplayDistinctFromScheduled()
    {
        var immediate = Request with { OpeningPolicy = new("immediate", null) };
        var result = await Service.CreateAsync(9, Key.ToString(), immediate, CancellationToken.None);
        Assert.Equal("immediate", result.Event.OpeningPolicy.Mode);
        Assert.Equal("open", result.Event.OpeningPolicy.ContentState);
        Assert.Null(result.Event.OpeningPolicy.OpensOn);
        Assert.True((await Service.CreateAsync(9, Key.ToString(), immediate, CancellationToken.None)).IsReplay);
        await Assert.ThrowsAsync<EventCreationKeyReusedException>(
            () => Service.CreateAsync(9, Key.ToString(), Request, CancellationToken.None));
    }

    [Fact]
    public async Task ListsWithAnAccountBoundBoundaryAndChecksDetailScope()
    {
        var token = CancellationToken.None;
        await Service.CreateAsync(9, Key.ToString(), Request, token);
        repository.ShowAnotherRow = true;
        var page = await Service.ListAsync(9, null, "1", token);
        Assert.Single(page.Items);
        Assert.Equal("9:11", page.NextCursor);
        Assert.Empty((await Service.ListAsync(9, page.NextCursor, "1", token)).Items);
        Assert.Equal("cursor", (await Assert.ThrowsAsync<EventRequestValidationException>(
            () => Service.ListAsync(19, page.NextCursor, "1", token))).Field);
        Assert.Equal("pageSize", (await Assert.ThrowsAsync<EventRequestValidationException>(
            () => Service.ListAsync(9, null, "51", token))).Field);
        await Assert.ThrowsAsync<EventNotFoundException>(() => Service.GetAsync(19, "11", token));
        await Assert.ThrowsAsync<EventRequestValidationException>(() => Service.GetAsync(9, "00011", token));
        Assert.Equal("11", (await Service.GetAsync(9, "11", token)).Id);
    }
}
