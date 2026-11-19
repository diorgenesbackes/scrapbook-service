using Microsoft.EntityFrameworkCore;
using Npgsql;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Infrastructure.Persistence;

namespace ScrapBook.Infrastructure.Probes;

public sealed class EfPostgresReadinessProbe(ScrapBookDbContext context) : IPostgresReadinessProbe
{
    private const string SchemaProbe = """
        SELECT current_user = 'scrapbook_app'
            AND count(*) = 24
            AND COALESCE(bool_and(has_schema_privilege(n.oid, 'USAGE')), FALSE)
            AND COALESCE(bool_and(
                CASE
                    WHEN c.relname = 'audit_entries' THEN has_table_privilege(c.oid, 'INSERT')
                    WHEN c.relname IN ('event_members', 'consent_notices')
                        THEN has_table_privilege(c.oid, 'SELECT') AND has_table_privilege(c.oid, 'INSERT')
                    ELSE has_table_privilege(c.oid, 'SELECT') AND has_table_privilege(c.oid, 'INSERT')
                        AND has_table_privilege(c.oid, 'UPDATE')
                END
            ), FALSE) AS "Value"
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'scrapbook' AND c.relkind = 'r'
          AND c.relname IN (
            'accounts', 'sessions', 'oidc_requests', 'account_operations',
            'events', 'event_members', 'invitations', 'covers', 'opening_dispatches',
            'consent_notices', 'records', 'idempotent_operations', 'upload_sessions',
            'files', 'storage_objects', 'file_variants', 'processing_jobs',
            'processing_attempts', 'cleanup_jobs', 'notifications', 'notification_attempts',
            'outbox_messages', 'processed_messages', 'audit_entries'
          )
        """;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await context.Database.SqlQueryRaw<bool>(SchemaProbe).SingleAsync(cancellationToken);
        }
        catch (Exception error) when (GetPostgresError(error) is { } postgresError)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (postgresError.InnerException is TimeoutException)
            {
                throw new PostgresReadinessTimeoutException(error);
            }

            throw new PostgresUnavailableException(error);
        }
    }

    private static NpgsqlException? GetPostgresError(Exception error) => error switch
    {
        NpgsqlException postgresError => postgresError,
        InvalidOperationException { InnerException: NpgsqlException postgresError } => postgresError,
        _ => null
    };
}
