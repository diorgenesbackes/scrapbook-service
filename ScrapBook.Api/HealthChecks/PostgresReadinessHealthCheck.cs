using Microsoft.Extensions.Diagnostics.HealthChecks;
using ScrapBook.Api.Options;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Api.HealthChecks;

public sealed class PostgresReadinessHealthCheck(
    IPostgresReadinessProbe probe,
    DatabaseOptions options,
    ILogger<PostgresReadinessHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.ReadinessTimeout);
        try
        {
            if (await probe.IsReadyAsync(budget.Token))
            {
                return HealthCheckResult.Healthy();
            }

            return Unavailable("database_not_ready");
        }
        catch (PostgresUnavailableException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Unavailable("database_unavailable");
        }
        catch (PostgresReadinessTimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Unavailable("database_timeout");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            return Unavailable("database_timeout");
        }
    }

    private HealthCheckResult Unavailable(string reason)
    {
        logger.LogWarning("Ambiente indisponível. Motivo {ReadinessReason}", reason);
        return HealthCheckResult.Unhealthy(reason);
    }
}
