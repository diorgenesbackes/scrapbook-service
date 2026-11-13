namespace ScrapBook.Application.Interfaces;

public interface IAccountRepository
{
    Task<bool> IsActiveDevelopmentAccountAsync(long accountId, CancellationToken cancellationToken);
}
