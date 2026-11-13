using ScrapBook.Api.Middlewares;
using ScrapBook.Application.Enums;
using ScrapBook.Infrastructure.Mappers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ScrapBook.UnitTests.Mappers;

public sealed class PersistenceExceptionMapperTests
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
        var failure = PersistenceExceptionMapper.Translate(new PostgresException("SYNTHETIC_PRIVATE", "ERROR", "ERROR", state));
        Assert.Equal(EventFailureKindEnum.DependencyUnavailable, failure.Kind);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", failure.Message);
    }

    [Fact]
    public void KnownConstraintUsesItsIdentifierInsteadOfMessage()
    {
        var error = new PostgresException("SYNTHETIC_PRIVATE", "ERROR", "ERROR", "23514",
            null, null, 0, 0, null, null, null, null, null, null, "ck_events_name", null, null, null);
        Assert.Equal(EventFailureKindEnum.Validation, PersistenceExceptionMapper.Translate(new DbUpdateException("hidden", error)).Kind);
        Assert.Equal(EventFailureKindEnum.Unexpected,
            PersistenceExceptionMapper.Translate(new PostgresException("Abertura programada travada", "ERROR", "ERROR", "23514")).Kind);
    }

    [Fact]
    public void OptimisticConflictIsDistinctFromDependencyFailure() =>
        Assert.Equal(EventFailureKindEnum.VersionConflict, PersistenceExceptionMapper.Translate(new DbUpdateConcurrencyException()).Kind);

    [Fact]
    public void ProviderWrappedConnectionFailureIsStillSanitized()
    {
        var error = new InvalidOperationException("private provider diagnostics",
            new PostgresException("private connection diagnostics", "FATAL", "FATAL", "57P01"));
        Assert.Equal(EventFailureKindEnum.DependencyUnavailable, PersistenceExceptionMapper.Translate(error).Kind);
        Assert.Equal(EventFailureKindEnum.DependencyUnavailable, ScrapBook.Api.Middlewares.EventRequestMiddleware.Sanitize(error).Kind);
    }
}
