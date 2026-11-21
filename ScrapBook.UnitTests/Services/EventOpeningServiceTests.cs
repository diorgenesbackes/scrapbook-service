using System.Text.Json;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.UnitTests.Services;

public sealed class EventOpeningServiceTests
{
    private readonly EventOpeningTestRepository repository = new();
    private readonly EventTestTimeProvider clock = new();
    private readonly Guid correlation = Guid.Parse("fb87bb93-f523-4983-ae41-9af133164dba");
    private EventOpeningService Service => new(repository, clock);

    [Theory]
    [InlineData("14:59:59", "scheduled", "flexible", true)]
    [InlineData("15:00:00", "scheduled", "scheduled_locked", false)]
    [InlineData("16:00:00", "scheduled", "scheduled_locked", false)]
    [InlineData("14:59:59", "immediate", "flexible", true)]
    [InlineData("15:00:00", "immediate", "immediate_once", true)]
    [InlineData("16:00:00", "immediate", "immediate_once", true)]
    public async Task EvaluatesBrasiliaCutoffOnAnUnchangedPolicy(string utcTime, string mode, string state, bool canEdit)
    {
        clock.Now = DateTimeOffset.Parse("2032-03-01T" + utcTime + "Z");
        repository.Item = Event(mode);
        var date = mode == "scheduled" ? "2033-02-28" : null;
        var result = await Service.ReplaceAsync(9, "11", new(mode, date, "0"), correlation, CancellationToken.None);
        Assert.Equal(state, result.OpeningPolicy.State);
        Assert.Equal(canEdit, result.OpeningPolicy.CanEdit);
        Assert.Null(repository.Audit);
        Assert.Equal("0", result.Version);
        Assert.False(repository.Transaction.Committed);
    }

    [Fact]
    public async Task AcceptsSinglePostCutoffTransitionAndRecordsBeforeAndAfter()
    {
        clock.Now = DateTimeOffset.Parse("2032-03-01T15:00:00Z");
        repository.Item = Event("immediate");
        var result = await Service.ReplaceAsync(9, "11", new("scheduled", "2033-02-28", "0"), correlation, CancellationToken.None);
        Assert.Equal("scheduled_locked", result.OpeningPolicy.State);
        Assert.False(result.OpeningPolicy.CanEdit);
        Assert.True(repository.Transaction.Committed);
        Assert.Equal(2, repository.Item.AccessVersion);
        Assert.Equal(new DateTime(2032, 3, 1, 12, 0, 0), repository.Item.AccessFrozenAt);
        var audit = Assert.IsType<AuditEntryEntity>(repository.Audit);
        Assert.Equal(correlation, audit.CorrelationId);
        Assert.Equal(clock.Now.AddDays(180), audit.ExpiresAt);
        Assert.Equal("event.opening.changed", audit.Action);
        using var before = JsonDocument.Parse(audit.BeforeState);
        using var after = JsonDocument.Parse(audit.AfterState);
        Assert.Equal("immediate", before.RootElement.GetProperty("mode").GetString());
        Assert.Equal("scheduled", after.RootElement.GetProperty("mode").GetString());
        Assert.Equal("2", after.RootElement.GetProperty("version").GetString());
        await Assert.ThrowsAsync<EventOpeningLockedException>(() =>
            Service.ReplaceAsync(9, "11", new("immediate", null, "1"), correlation, CancellationToken.None));
    }

    [Fact]
    public async Task FlexibleOpeningRemainsEditableAndRejectsStaleVersion()
    {
        clock.Now = DateTimeOffset.Parse("2032-03-01T14:59:59Z");
        repository.Item = Event("scheduled");
        var result = await Service.ReplaceAsync(9, "11", new("immediate", null, "0"), correlation, CancellationToken.None);
        Assert.Equal("flexible", result.OpeningPolicy.State);
        Assert.Null(repository.Item.AccessFrozenAt);
        await Assert.ThrowsAsync<EventVersionConflictException>(() =>
            Service.ReplaceAsync(9, "11", new("immediate", null, "0"), correlation, CancellationToken.None));
    }

    [Theory]
    [InlineData("2032-02-28")]
    [InlineData("2032-03-01")]
    [InlineData("2033-02-29")]
    public async Task RejectsDatesBeforeTheEndOfSubmissionsOrInvalid(string date)
    {
        clock.Now = DateTimeOffset.Parse("2032-02-29T12:00:00Z");
        repository.Item = Event("immediate");
        var error = await Assert.ThrowsAsync<EventRequestValidationException>(() =>
            Service.ReplaceAsync(9, "11", new("scheduled", date, "0"), correlation, CancellationToken.None));
        Assert.Equal("opensOn", error.Field);
        Assert.Null(repository.Audit);
    }

    [Fact]
    public async Task RefusesNonMemberAndDoesNotCommitWhenAuditFails()
    {
        repository.Item = Event("immediate");
        await Assert.ThrowsAsync<EventNotFoundException>(() =>
            Service.ReplaceAsync(19, "11", new("scheduled", "2033-02-28", "0"), correlation, CancellationToken.None));
        Assert.Null(repository.Audit);
        repository.FailAudit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ReplaceAsync(9, "11", new("scheduled", "2033-02-28", "0"), correlation, CancellationToken.None));
        Assert.False(repository.Transaction.Committed);
        Assert.True(repository.Transaction.Disposed);
    }

    private static EventEntity Event(string mode) => new()
    {
        Id = 11,
        Name = "Festa",
        Code = new string('a', 43),
        OpeningMode = mode,
        OpensAt = mode == "scheduled" ? new DateTime(2033, 2, 28) : null,
        SubmissionClosesAt = new DateTime(2032, 3, 1, 12, 0, 0),
        AccessState = "flexible"
    };
}
