using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.UnitTests.Services;

public sealed class EventDetailsServiceTests
{
    private readonly EventTestTimeProvider clock = new();
    private readonly EventWriteTestRepository repository = new();
    private EventDetailsService Service => new(repository, clock);

    [Fact]
    public async Task UpdatesTheBrasiliaWindowAndKeepsThePublicCode()
    {
        var item = repository.Item = Event();
        var result = await ReplaceAsync(new("  Novo evento  ", "2032-02-29T01:30:00", "  Olá! 🎉  ", "0"));

        Assert.Equal("Novo evento", result.Name);
        Assert.Equal("Olá! 🎉", result.WelcomeMessage);
        Assert.False(result.UsesDefaultWelcomeMessage);
        Assert.Equal("2032-02-28T23:30:00", result.SubmissionOpensAt);
        Assert.Equal("2032-03-01T12:00:00", result.SubmissionClosesAt);
        Assert.Equal("2032-03-01T13:00:00", result.CompletionDeadlineAt);
        Assert.Equal("1", result.Version);
        Assert.Equal("2033-02-28", result.OpeningPolicy.OpensOn);
        Assert.Equal("flexible", result.OpeningPolicy.State);
        Assert.Equal(new string('a', 43), item.Code);
        Assert.Equal(1, item.AccessVersion);
        Assert.True(repository.Transaction.Committed);
    }

    [Theory]
    [InlineData("scheduled", "scheduled_locked")]
    [InlineData("immediate", "immediate_once")]
    public async Task KeepsAnEffectiveFreezeWhenNewWindowWouldReopenThePolicy(string mode, string state)
    {
        var item = repository.Item = Event();
        item.OpeningMode = mode;
        item.OpensAt = mode == "scheduled" ? item.OpensAt : null;
        clock.Now = DateTimeOffset.Parse("2032-01-02T15:00:00Z");

        var result = await ReplaceAsync(new("Festa", "2032-02-29T01:30:00", null, "0"));
        Assert.Equal(state, result.OpeningPolicy.State);
        Assert.Equal(state, item.AccessState);
        Assert.Equal(new DateTime(2032, 1, 2, 12, 0, 0), item.AccessFrozenAt);
    }

    [Fact]
    public async Task MaterializesTheNewCutoffWhenTheOldOneIsStillFlexible()
    {
        var item = repository.Item = Event();
        clock.Now = DateTimeOffset.Parse("2031-12-30T15:00:00Z");

        await ReplaceAsync(new("Festa", "2031-12-29T18:00:00", null, "0"));
        Assert.Equal("scheduled_locked", item.AccessState);
        Assert.Equal(new DateTime(2031, 12, 30, 12, 0, 0), item.AccessFrozenAt);
    }

    [Fact]
    public async Task KeepsAnExistingFreezeMarkerAndRestoresDefaultWelcome()
    {
        var item = repository.Item = Event();
        item.WelcomeMessage = "Mensagem anterior";
        item.AccessState = "scheduled_locked";
        item.AccessFrozenAt = new DateTime(2031, 12, 20, 12, 0, 0);

        var result = await ReplaceAsync(new("Festa", "2032-02-29T01:30:00", null, "0"));
        Assert.Equal("scheduled_locked", item.AccessState);
        Assert.Equal(new DateTime(2031, 12, 20, 12, 0, 0), item.AccessFrozenAt);
        Assert.True(result.UsesDefaultWelcomeMessage);
        Assert.Null(item.WelcomeMessage);
    }

    [Theory]
    [InlineData(" ", "2032-02-29T01:30:00", null, "0", "name")]
    [InlineData("Festa", "2032-02-30T01:30:00", null, "0", "startsAt")]
    [InlineData("Festa", "2032-02-29T01:30:00", " ", "0", "welcomeMessage")]
    [InlineData("Festa", "2032-02-29T01:30:00", "Oi\0", "0", "welcomeMessage")]
    [InlineData("Festa", "2032-02-29T01:30:00", null, "-1", "expectedVersion")]
    [InlineData("Festa", "2033-02-27T18:00:00", null, "0", "startsAt")]
    public async Task RejectsInvalidFieldsWithoutWriting(string name, string starts, string? message, string version, string field)
    {
        repository.Item = Event();
        var error = await Assert.ThrowsAsync<EventRequestValidationException>(() =>
            ReplaceAsync(new(name, starts, message, version)));
        Assert.Equal(field, error.Field);
        Assert.Equal("Festa", repository.Item.Name);
        Assert.Equal(0, repository.Item.Version);
        Assert.False(repository.Transaction.Committed);
    }

    [Fact]
    public async Task CountsUnicodeCharactersForWelcomeLimits()
    {
        repository.Item = Event();
        var maximum = string.Concat(Enumerable.Repeat("🎉", 2000));
        var response = await ReplaceAsync(new("Festa", "2032-01-01T18:00:00", maximum, "0"));
        Assert.Equal(maximum, response.WelcomeMessage);
        var error = await Assert.ThrowsAsync<EventRequestValidationException>(() =>
            ReplaceAsync(new("Festa", "2032-01-01T18:00:00", maximum + "🎉", "1")));
        Assert.Equal("welcomeMessage", error.Field);
    }

    [Fact]
    public async Task DistinguishesNoOpStaleVersionStructuralLockAndMissingMembership()
    {
        var request = new ReplaceEventDetailsRequest("Festa", "2032-01-01T18:00:00", null, "0");
        repository.Item = Event();
        Assert.Equal("0", (await ReplaceAsync(request)).Version);
        Assert.False(repository.Transaction.Committed);

        await Assert.ThrowsAsync<EventVersionConflictException>(() => ReplaceAsync(request with { ExpectedVersion = "1" }));
        await Assert.ThrowsAsync<EventNotFoundException>(() =>
            Service.ReplaceAsync(19, "11", request, CancellationToken.None));
        repository.Item = Event(new DateTime(2031, 12, 31));
        await Assert.ThrowsAsync<EventStructuralLockedException>(() => ReplaceAsync(request));
        Assert.False(repository.Transaction.Committed);
        Assert.True(repository.Transaction.Disposed);
    }

    private Task<EventResponse> ReplaceAsync(ReplaceEventDetailsRequest request) =>
        Service.ReplaceAsync(9, "11", request, CancellationToken.None);

    private static EventEntity Event(DateTime? lockedAt = null) => new()
    {
        Id = 11,
        Name = "Festa",
        Code = new string('a', 43),
        StartsAt = new DateTime(2032, 1, 1, 18, 0, 0),
        SubmissionClosesAt = new DateTime(2032, 1, 2, 12, 0, 0),
        OpeningMode = "scheduled",
        OpensAt = new DateTime(2033, 2, 28),
        AccessState = "flexible",
        LockedAt = lockedAt
    };
}
