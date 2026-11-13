using ScrapBook.Application.Interfaces;
namespace ScrapBook.UnitTests.Stubs;

internal sealed class AccountRepositoryStub(bool active) : IAccountRepository
{
    public int Calls { get; private set; }
    public Task<bool> IsActiveDevelopmentAccountAsync(long accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(active);
    }
}
