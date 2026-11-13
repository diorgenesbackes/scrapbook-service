using ScrapBook.Application.Services;
using ScrapBook.UnitTests.Stubs;
namespace ScrapBook.UnitTests.Services;

public sealed class DevelopmentCurrentActorServiceTests
{
    [Fact]
    public async Task DisabledIdentityDoesNotReadAccounts()
    {
        var reader = new AccountRepositoryStub(true);
        Assert.Null(await new DevelopmentCurrentActorService(false, 42, reader).ResolveAsync(default));
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(true, 42)]
    [InlineData(false, 0)]
    public async Task OnlyValidatedFixtureResolvesAnActor(bool active, long expected)
    {
        var reader = new AccountRepositoryStub(active);
        var result = await new DevelopmentCurrentActorService(true, 42, reader).ResolveAsync(default);
        Assert.Equal(expected, result?.AccountId ?? 0);
        Assert.Equal(1, reader.Calls);
    }

}
