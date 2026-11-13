using ScrapBook.Application.Modules.Identity;
using ScrapBook.Infrastructure.Modules.Identity;

namespace ScrapBook.UnitTests.Modules.Identity;

public sealed class DevelopmentCurrentActorTests
{
    [Fact]
    public async Task DisabledIdentityDoesNotReadAccounts()
    {
        var reader = new AccountReader(true);
        Assert.Null(await new DevelopmentCurrentActor(false, 42, reader).ResolveAsync(default));
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(true, 42)]
    [InlineData(false, 0)]
    public async Task OnlyValidatedFixtureResolvesAnActor(bool active, long expected)
    {
        var reader = new AccountReader(active);
        var result = await new DevelopmentCurrentActor(true, 42, reader).ResolveAsync(default);
        Assert.Equal(expected, result?.AccountId ?? 0);
        Assert.Equal(1, reader.Calls);
    }

    private sealed class AccountReader(bool active) : IDevelopmentAccountReader
    {
        public int Calls { get; private set; }
        public Task<bool> IsActiveDevelopmentAccountAsync(long accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(active);
        }
    }
}
