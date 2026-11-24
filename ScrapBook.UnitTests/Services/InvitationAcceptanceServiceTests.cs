using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Responses;
using ScrapBook.Infrastructure.Codecs;

namespace ScrapBook.UnitTests.Services;

public sealed class InvitationAcceptanceServiceTests
{
    private readonly EventWriteTestRepository events = new() { Item = BuildEvent() };
    private readonly InvitationTestRepository invitations = new() { MemberId = null };
    private readonly EventTestTimeProvider clock = new();
    private readonly InvitationTokenCodec codec = new("hash", new Dictionary<string, byte[]> { ["hash"] = new byte[32] },
        "cipher", new Dictionary<string, byte[]> { ["cipher"] = [.. Enumerable.Repeat((byte)1, 32)] });
    private readonly InvitationEntity issued;
    private readonly string token;

    public InvitationAcceptanceServiceTests()
    {
        var key = Guid.NewGuid();
        var secret = codec.Create(42, key);
        issued = new InvitationEntity
        {
            Id = 31,
            EventId = 42,
            IssuerMemberId = 12,
            RequestKey = key,
            HashKeyId = secret.HashKeyId,
            TokenHash = secret.Hash,
            EncryptionKeyId = secret.EncryptionKeyId,
            TokenCiphertext = secret.Ciphertext,
            State = "pending",
            CreatedAt = clock.Now,
            ExpiresAt = clock.Now.AddHours(24)
        };
        invitations.Items.Add(issued);
        token = codec.Recover(issued);
    }

    private InvitationAcceptanceService Service => new(events, invitations, codec, clock);

    [Fact]
    public async Task PreviewDoesNotConsumeAndAcceptanceRecordsOnlyOneSecondOwnerAsync()
    {
        var preview = await PreviewAsync();
        Assert.Equal(new InvitationPreviewResponse("eligible", "Evento sintético", issued.ExpiresAt), preview);
        Assert.Null(invitations.NewMember);
        Assert.Null(events.Audit);

        Assert.Equal(new InvitationAcceptanceResponse("accepted", "42"), await AcceptAsync());
        Assert.Equal((42L, 10L, (short)2), (invitations.NewMember!.EventId, invitations.NewMember.AccountId, invitations.NewMember.Slot));
        Assert.Equal(10, issued.RecipientAccountId);
        Assert.Equal(clock.Now, issued.AcceptedAt);
        Assert.Null(issued.TokenCiphertext);
        Assert.Null(issued.EncryptionKeyId);
        var audit = events.Audit;
        Assert.NotNull(audit);
        Assert.Equal(31, audit.SubjectId);
        Assert.DoesNotContain(token, audit.AfterState);

        clock.Now = issued.ExpiresAt.AddHours(1);
        events.Item = BuildEvent(clock.Now.UtcDateTime);
        Assert.Equal(new InvitationPreviewResponse("accepted", EventId: "42"), await PreviewAsync());
        Assert.Equal(new InvitationAcceptanceResponse("accepted", "42"), await AcceptAsync());
        Assert.Same(audit, events.Audit);
        Assert.Equal(2, invitations.MemberCount);
        await Assert.ThrowsAsync<InvitationAlreadyUsedException>(() => AcceptAsync(11));
        invitations.MemberId = null;
        await Assert.ThrowsAsync<InvitationAlreadyUsedException>(() => AcceptAsync());
    }

    [Fact]
    public async Task ChecksAccountBeforeReturningInformationAboutATokenAsync()
    {
        invitations.EmailVerified = false;
        await Assert.ThrowsAsync<InvitationAccountUnconfirmedException>(() =>
            Service.PreviewAsync(10, "invalid", CancellationToken.None));
        invitations.Active = false;
        await Assert.ThrowsAsync<EventIdentityRequiredException>(() => AcceptAsync());
        Assert.Null(invitations.NewMember);
    }

    [Fact]
    public async Task RejectsUnknownTokensAndWrongEventEvenWithAConfirmedAccountAsync()
    {
        await Assert.ThrowsAsync<InvitationNotFoundException>(() =>
            Service.PreviewAsync(10, token[..^1] + (token[^1] == 'A' ? "B" : "A"), CancellationToken.None));
        events.Item = null;
        await Assert.ThrowsAsync<InvitationNotFoundException>(() => AcceptAsync());
        Assert.Null(invitations.NewMember);
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("expired")]
    [InlineData("invalidated")]
    public async Task RejectsTerminalInvitationsAsync(string state)
    {
        issued.State = state;
        await Assert.ThrowsAsync<InvitationUnavailableException>(() => AcceptAsync());
        Assert.Null(invitations.NewMember);
    }

    [Fact]
    public async Task StopsAtTheExactExpiryAndSeparatesFullLockedAndLinkedEventsAsync()
    {
        clock.Now = issued.ExpiresAt;
        await Assert.ThrowsAsync<InvitationUnavailableException>(() => AcceptAsync());
        clock.Now = issued.ExpiresAt.AddTicks(-1);
        Assert.Equal("eligible", (await PreviewAsync()).State);
        invitations.MemberCount = 2;
        await Assert.ThrowsAsync<InvitationEventFullException>(() => AcceptAsync());
        invitations.MemberCount = 1;
        invitations.MemberId = issued.IssuerMemberId;
        await Assert.ThrowsAsync<InvitationAccountAlreadyLinkedException>(() => AcceptAsync());
        events.Item = BuildEvent(clock.Now.UtcDateTime);
        await Assert.ThrowsAsync<InvitationEventLockedException>(() => AcceptAsync());
        Assert.Null(invitations.NewMember);
    }

    [Fact]
    public async Task KeepsCancellationAndNeverCommitsAfterAnAuditErrorAsync()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.PreviewAsync(10, token, canceled.Token));

        events.FailAudit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync());
        Assert.False(events.Transaction.Committed);
        Assert.True(events.Transaction.Disposed);
    }

    private Task<InvitationPreviewResponse> PreviewAsync() => Service.PreviewAsync(10, token, CancellationToken.None);
    private Task<InvitationAcceptanceResponse> AcceptAsync(long accountId = 10) =>
        Service.AcceptAsync(accountId, token, Guid.NewGuid(), CancellationToken.None);

    private static EventEntity BuildEvent(DateTime? lockedAt = null) => new()
    {
        Id = 42,
        Code = new string('A', 43),
        Name = "Evento sintético",
        OpeningMode = "immediate",
        AccessState = "flexible",
        LockedAt = lockedAt
    };
}
