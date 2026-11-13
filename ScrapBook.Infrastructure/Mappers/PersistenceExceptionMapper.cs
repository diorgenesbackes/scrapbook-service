using ScrapBook.Application.Enums;
using ScrapBook.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ScrapBook.Infrastructure.Mappers;

public static class PersistenceExceptionMapper
{
    private static readonly HashSet<string> ValidationConstraints =
        ["ck_events_name", "ck_events_window", "ck_events_opening", "ck_events_version", "ck_event_members_slot"];

    public static EventApplicationException Translate(Exception error)
    {
        if (error is DbUpdateConcurrencyException) return new(EventFailureKindEnum.VersionConflict);
        var cause = error switch
        {
            DbUpdateException { InnerException: not null } update => update.InnerException,
            InvalidOperationException { InnerException: NpgsqlException databaseError } => databaseError,
            _ => error
        };
        if (cause is PostgresException postgres)
        {
            if (postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
                || postgres.SqlState is "28000" or "28P01" or "55P03" or "57014" or "40001" or "40P01" or "53300" or "57P01" or "57P02" or "57P03")
                return new(EventFailureKindEnum.DependencyUnavailable);
            if (postgres.SqlState == "23514" && postgres.ConstraintName is not null
                && ValidationConstraints.Contains(postgres.ConstraintName))
                return new(EventFailureKindEnum.Validation);
            return new(EventFailureKindEnum.Unexpected);
        }

        return new(cause is NpgsqlException or TimeoutException or OperationCanceledException
            ? EventFailureKindEnum.DependencyUnavailable : EventFailureKindEnum.Unexpected);
    }
}
