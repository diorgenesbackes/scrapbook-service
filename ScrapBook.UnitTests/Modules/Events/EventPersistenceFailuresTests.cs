using Microsoft.EntityFrameworkCore;
using Npgsql;
using ScrapBook.Application.Modules.Events;
using ScrapBook.Infrastructure.Modules.Events.Persistence;

namespace ScrapBook.UnitTests.Modules.Events;

public sealed class EventPersistenceFailuresTests
{
    [Theory]
    [InlineData("55P03")]
    [InlineData("57014")]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("28000")]
    [InlineData("28P01")]
    [InlineData("08006")]
    [InlineData("57P03")]
    public void KnownTransientStatesAreSanitizedWithoutRetry(string state)
    {
        var failure = EventPersistenceFailures.Translate(new PostgresException("SYNTHETIC_PRIVATE", "ERROR", "ERROR", state));
        Assert.Equal(EventFailureKind.DependencyUnavailable, failure.Kind);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", failure.Message);
    }

    [Fact]
    public void KnownConstraintUsesItsIdentifierInsteadOfMessage()
    {
        var error = new PostgresException("SYNTHETIC_PRIVATE", "ERROR", "ERROR", "23514",
            null, null, 0, 0, null, null, null, null, null, null, "ck_events_name", null, null, null);
        Assert.Equal(EventFailureKind.Validation, EventPersistenceFailures.Translate(new DbUpdateException("hidden", error)).Kind);
        Assert.Equal(EventFailureKind.Unexpected,
            EventPersistenceFailures.Translate(new PostgresException("Abertura programada travada", "ERROR", "ERROR", "23514")).Kind);
    }

    [Fact]
    public void OptimisticConflictIsDistinctFromDependencyFailure() =>
        Assert.Equal(EventFailureKind.VersionConflict, EventPersistenceFailures.Translate(new DbUpdateConcurrencyException()).Kind);

    [Fact]
    public void ProviderWrappedConnectionFailureIsStillSanitized()
    {
        var error = new InvalidOperationException("private provider diagnostics",
            new PostgresException("private connection diagnostics", "FATAL", "FATAL", "57P01"));
        Assert.Equal(EventFailureKind.DependencyUnavailable, EventPersistenceFailures.Translate(error).Kind);
        Assert.Equal(EventFailureKind.DependencyUnavailable, ScrapBook.Api.Modules.Events.EventsBoundary.Sanitize(error).Kind);
    }
}
