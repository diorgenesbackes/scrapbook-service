namespace ScrapBook.Domain.Interfaces;

public interface IPostgresReadinessProbe
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}
