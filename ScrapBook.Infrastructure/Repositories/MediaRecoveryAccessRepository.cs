using System.Text.Json;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Repositories;

public sealed class MediaRecoveryAccessRepository(MediaPreparationRepository repository) : IMediaRecoveryAccessRepository
{
    public Task<string?> FindOperatorAsync(CancellationToken cancellationToken) =>
        repository.QueryAsync<string>($"""
            SELECT identity.rolname::text AS "Value"
            FROM pg_catalog.pg_roles AS identity
            JOIN pg_catalog.pg_roles AS permitted ON permitted.rolname = 'scrapbook_media_operator'
            WHERE identity.rolname = session_user AND current_user = session_user
                AND identity.rolcanlogin AND NOT identity.rolsuper AND NOT identity.rolcreaterole
                AND NOT identity.rolcreatedb AND NOT identity.rolbypassrls AND NOT identity.rolreplication
                AND identity.rolname NOT IN ('scrapbook_app', 'scrapbook_migrator')
                AND pg_catalog.pg_has_role(identity.oid, permitted.oid, 'MEMBER')
            """, cancellationToken);

    public Task AuditAsync(MediaRecoveryAuditModel audit, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            databasePrincipal = audit.Operator,
            incident = audit.Incident,
            correctiveRevision = audit.Revision,
            cycle = audit.Cycle,
            messageId = audit.MessageId,
            requestedEventId = audit.EventId
        });
        return repository.RequireWriteAsync($"""
            INSERT INTO scrapbook.audit_entries (
                audit_entries_events_id, actor_type, action, subject_type, subject_id, outcome,
                reason_code, correlation_id, after_state, expires_at, area
            )
            VALUES ((SELECT id FROM scrapbook.events WHERE id = {audit.EventId}), 'system', {audit.Action}, 'file', {audit.FileId}, {audit.Outcome},
                {audit.ReasonCode}, {audit.CorrelationId}, CAST({data} AS jsonb), CURRENT_TIMESTAMP + INTERVAL '4320 hours', 'records')
            """, cancellationToken);
    }
}
