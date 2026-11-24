using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Codecs;

namespace ScrapBook.UnitTests.Services;

public sealed class InvitationServiceTests
{
    private readonly EventWriteTestRepository events = new() { Item = OpenEvent() };
    private readonly InvitationTestRepository invitations = new();
    private readonly EventTestTimeProvider clock = new();
    private readonly InvitationTokenCodec codec = new("test", new Dictionary<string, byte[]> { ["test"] = new byte[32] }, "cipher",
        new Dictionary<string, byte[]> { ["cipher"] = [.. Enumerable.Repeat((byte)1, 32)] });
    private InvitationService Service => new(events, invitations, codec, clock);

    [Fact]
    public async Task ReplaysTheSamePendingInvitationWithoutExtendingItsExpiryAsync()
    {
        var key = Guid.NewGuid().ToString();
        var persistedDeadline = clock.Now.AddHours(24);
        clock.Now = clock.Now.AddTicks(7);
        var original = await IssueAsync(key);
        Assert.False(original.IsReplay);
        Assert.Equal(persistedDeadline, original.Invitation.ExpiresAt);
        Assert.Equal(original.Invitation, (await CurrentAsync()).Invitation);
        var audit = events.Audit;
        Assert.NotNull(audit);
        Assert.Equal(1, audit.SubjectId);
        Assert.DoesNotContain(original.Invitation.Token, audit.AfterState);

        clock.Now = clock.Now.AddHours(1);
        var replay = await IssueAsync(key);
        Assert.True(replay.IsReplay);
        Assert.Equal(original.Invitation, replay.Invitation);
        Assert.Same(audit, events.Audit);
        Assert.Single(invitations.Items);
        Assert.True(events.Transaction.Committed);
    }

    [Theory]
    [InlineData(23, "superseded")]
    [InlineData(24, "expired")]
    public async Task ReplacesThePriorInvitationAndRemovesItsRecoverableSecretAsync(int elapsedHours, string terminal)
    {
        var priorKey = Guid.NewGuid().ToString();
        await IssueAsync(priorKey);
        clock.Now = clock.Now.AddHours(elapsedHours);
        var replacement = await IssueAsync(Guid.NewGuid().ToString());
        var previous = invitations.Items[0];
        Assert.Equal(terminal, previous.State);
        Assert.Null(previous.TokenCiphertext);
        Assert.Null(previous.EncryptionKeyId);
        Assert.Equal(clock.Now, previous.InvalidatedAt);
        Assert.Equal(replacement.Invitation, (await CurrentAsync()).Invitation);
        await Assert.ThrowsAsync<InvitationUnavailableException>(() => IssueAsync(priorKey));
    }

    [Fact]
    public async Task HidesTheInvitationAtItsExactExpiryAndRejectsTheOldKeyAsync()
    {
        var key = Guid.NewGuid().ToString();
        var creation = await IssueAsync(key);
        clock.Now = creation.Invitation.ExpiresAt.AddTicks(-1);
        Assert.Equal("pending", (await CurrentAsync()).State);
        clock.Now = creation.Invitation.ExpiresAt;
        var after = await CurrentAsync();
        Assert.Equal("available", after.State);
        Assert.Null(after.Invitation);
        await Assert.ThrowsAsync<InvitationUnavailableException>(() => IssueAsync(key));
    }

    [Fact]
    public async Task HidesLockedAndFullInvitationsFromTheOwnerAsync()
    {
        var key = Guid.NewGuid().ToString();
        await IssueAsync(key);
        invitations.MemberCount = 2;
        Assert.Equal("full", (await CurrentAsync()).State);
        Assert.Null((await CurrentAsync()).Invitation);
        await Assert.ThrowsAsync<InvitationEventFullException>(() => IssueAsync(key));
        events.Item = OpenEvent(clock.Now.UtcDateTime);
        Assert.Equal("locked", (await CurrentAsync()).State);
        await Assert.ThrowsAsync<InvitationEventLockedException>(() => IssueAsync(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task AuthorizesBeforeRecoveringSecretsOrReplayingKeysAsync()
    {
        var key = Guid.NewGuid().ToString();
        await IssueAsync(key);
        await Assert.ThrowsAsync<EventNotFoundException>(() => Service.GetCurrentAsync(8, "42", CancellationToken.None));
        invitations.MemberId = 33;
        await Assert.ThrowsAsync<InvitationKeyReusedException>(() => IssueAsync(key));
        invitations.Active = false;
        await Assert.ThrowsAsync<EventIdentityRequiredException>(() => CurrentAsync());
    }

    [Fact]
    public async Task PreservesCancellationAndDoesNotCommitWhenAuditFailsAsync()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.GetCurrentAsync(9, "42", source.Token));
        events.FailAudit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => IssueAsync(Guid.NewGuid().ToString()));
        Assert.False(events.Transaction.Committed);
        Assert.True(events.Transaction.Disposed);
    }

    private Task<ScrapBook.Domain.Responses.CreateInvitationResponse> IssueAsync(string key) =>
        Service.CreateAsync(9, "42", key, Guid.NewGuid(), CancellationToken.None);

    private Task<ScrapBook.Domain.Responses.InvitationStatusResponse> CurrentAsync() =>
        Service.GetCurrentAsync(9, "42", CancellationToken.None);

    private static EventEntity OpenEvent(DateTime? lockedAt = null) => new()
    {
        Id = 42,
        Code = new string('A', 43),
        Name = "Evento sintético",
        OpeningMode = "immediate",
        AccessState = "flexible",
        LockedAt = lockedAt
    };
}
