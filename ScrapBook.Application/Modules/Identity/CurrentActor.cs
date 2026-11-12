namespace ScrapBook.Application.Modules.Identity;

public sealed record CurrentActor(long AccountId, string Source = "development_fixture");

public interface ICurrentActor
{
    Task<CurrentActor?> ResolveAsync(CancellationToken cancellationToken);
}

public interface IDevelopmentAccountReader
{
    Task<bool> IsActiveDevelopmentAccountAsync(long accountId, CancellationToken cancellationToken);
}
