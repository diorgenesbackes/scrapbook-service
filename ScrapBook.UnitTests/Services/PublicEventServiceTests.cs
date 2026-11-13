using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Models;
using ScrapBook.UnitTests.Stubs;

namespace ScrapBook.UnitTests.Services;

public sealed class PublicEventServiceTests
{
    private static readonly string Code = "A" + new string('a', 41) + "A";

    [Fact]
    public async Task ReturnsOnlyThePublicProjectionAndPropagatesCancellation()
    {
        var model = new PublicEventModel(Code);
        var repository = new PublicEventRepositoryStub(model);
        using var source = new CancellationTokenSource();
        Assert.Same(model, await new PublicEventService(repository).GetAsync(Code, source.Token));
        Assert.Equal(1, repository.Calls);
        Assert.Equal(Code, repository.Code);
        Assert.Equal(source.Token, repository.Token);
    }

    [Fact]
    public async Task MissingEventReturnsNotFound()
    {
        var error = await Assert.ThrowsAsync<EventApplicationException>(() =>
            new PublicEventService(new PublicEventRepositoryStub(null)).GetAsync(Code, default));
        Assert.Equal(EventFailureKindEnum.NotFound, error.Kind);
    }

    [Fact]
    public async Task InvalidCodeDoesNotQueryPersistence()
    {
        var repository = new PublicEventRepositoryStub(null);
        var error = await Assert.ThrowsAsync<EventApplicationException>(() => new PublicEventService(repository).GetAsync("1", default));
        Assert.Equal(EventFailureKindEnum.NotFound, error.Kind);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task DoesNotNormalizeCaseToFindAnotherIdentifier()
    {
        var repository = new PublicEventRepositoryStub(new PublicEventModel(Code));
        await Assert.ThrowsAsync<EventApplicationException>(() => new PublicEventService(repository).GetAsync("a" + Code[1..], default));
        Assert.Equal("a" + Code[1..], repository.Code);
    }

    [Fact]
    public async Task CancelledRequestDoesNotQueryPersistence()
    {
        var repository = new PublicEventRepositoryStub(null);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new PublicEventService(repository).GetAsync(Code, source.Token));
        Assert.Equal(0, repository.Calls);
    }
}
