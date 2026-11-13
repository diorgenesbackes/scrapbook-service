using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Models;
using ScrapBook.UnitTests.Stubs;

namespace ScrapBook.UnitTests.Services;

public sealed class EventSharingServiceTests
{
    [Fact]
    public async Task PassesTheActorAndReturnsOnlySharingFields()
    {
        var code = new string('A', 43);
        var repository = new EventSharingRepositoryStub(new(code, "Evento"));
        using var source = new CancellationTokenSource();
        var result = await new EventSharingService(repository, new("https://share.example.invalid"))
            .GetAsync(new(42), 99, source.Token);
        Assert.Equal(42, repository.AccountId); Assert.Equal(99, repository.EventId); Assert.Equal(source.Token, repository.Token);
        Assert.Equal(code, result.Code); Assert.Equal("Evento", result.Name);
        Assert.Equal("https://share.example.invalid/e/" + code, result.PublicUrl);
    }

    [Fact]
    public async Task ChecksMembershipBeforeExposingMissingConfiguration()
    {
        var error = await Assert.ThrowsAsync<EventApplicationException>(() => new EventSharingService(new EventSharingRepositoryStub(null), new(null))
            .GetAsync(new(42), 99, default));
        Assert.Equal(EventFailureKindEnum.NotFound, error.Kind);
    }

    [Fact]
    public async Task MissingOriginDoesNotInventAPublicLink()
    {
        var error = await Assert.ThrowsAsync<EventApplicationException>(() => new EventSharingService(new EventSharingRepositoryStub(new(new string('A', 43), "Evento")), new(null))
            .GetAsync(new(42), 99, default));
        Assert.Equal(EventFailureKindEnum.SharingUnavailable, error.Kind);
    }
}
