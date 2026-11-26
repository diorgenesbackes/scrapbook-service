using System.Text.Json;
using System.Text.Json.Nodes;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class EventStructuralLockServiceTests
{
    private const string FirstConfirmationType = "event.first-record-confirmed.v1";
    private static readonly Guid MessageId = Guid.Parse("3b0da52a-12ea-1258-ec0b-73e8530e90cf");
    private static readonly Guid CorrelationId = Guid.Parse("da771942-1b5b-4c4b-885a-62260ad1fc65");
    private static readonly DateTimeOffset ConfirmedAt = DateTimeOffset.Parse("2031-01-01T00:00:00.120Z");
    private static readonly DateTime ConfirmedInBrasilia = new(2030, 12, 31, 21, 0, 0, 120);
    private readonly EventTestTimeProvider clock = new() { Now = ConfirmedAt.AddSeconds(3) };
    private readonly EventWriteTestRepository events = new() { Item = Event() };
    private readonly InvitationTestRepository invitations = new();
    private readonly EventStructuralLockTestRepository locks = new() { ConfirmedAt = ConfirmedInBrasilia };
    private readonly OutboxTestRepository outbox = new() { Origin = Origin() };
    private EventStructuralLockService Service => new(events, invitations, locks, outbox, clock);

    [Fact]
    public async Task AppliesTheLockAndInvalidatesPendingInvitationInTheSameTransactionAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = Invitation("pending");
        invitations.Items.Add(pending);
        locks.BeforeCompletion = () =>
        {
            Assert.False(events.Transaction.Committed);
            Assert.Single(locks.Applications);
            Assert.Same(pending, Assert.Single(invitations.Closed));
            Assert.NotNull(events.Audit);
        };

        Assert.Equal(EventStructuralLockResult.Applied, await Service.ConsumeAsync(Envelope(), cancellation.Token));

        Assert.Equal((new EventStructuralLockCommand(42, 8, 237, ConfirmedInBrasilia, clock.Now), cancellation.Token),
            Assert.Single(locks.Applications));
        Assert.Equal((42L, 237L, cancellation.Token), Assert.Single(locks.RecordLookups));
        Assert.Equal(("event-structural-lock", "internal", MessageId, CorrelationId, cancellation.Token),
            Assert.Single(locks.Claims));
        Assert.Equal((73L, clock.Now, cancellation.Token), Assert.Single(locks.Completions));
        Assert.Equal("invalidated", pending.State);
        Assert.Equal(clock.Now, pending.InvalidatedAt);
        Assert.Equal(6, pending.Version);
        Assert.Null(pending.TokenCiphertext);
        Assert.Null(pending.EncryptionKeyId);
        Assert.Equal("hash-key", pending.HashKeyId);
        Assert.Equal(new byte[] { 1, 2, 3 }, pending.TokenHash);

        var audit = Assert.IsType<AuditEntryEntity>(events.Audit);
        Assert.Null(audit.AccountId);
        Assert.Equal((42L, "system", "event.structural_lock.applied", CorrelationId, clock.Now),
            (audit.EventId, audit.ActorType, audit.Action, audit.CorrelationId, audit.CreatedAt));
        using var before = JsonDocument.Parse(audit.BeforeState);
        using var after = JsonDocument.Parse(audit.AfterState);
        Assert.False(Assert.Single(before.RootElement.EnumerateObject()).Value.GetBoolean());
        Assert.True(Assert.Single(after.RootElement.EnumerateObject()).Value.GetBoolean());
        Assert.True(events.Transaction.Committed);
        Assert.True(events.Transaction.Disposed);
    }

    [Fact]
    public async Task VerifiesHistoricalOutboxRowsUsingTheStableMessageCorrelationAsync()
    {
        outbox.Origin = Origin() with { CorrelationId = Guid.Empty };
        var body = JsonNode.Parse(Envelope())!.AsObject();
        body["correlationId"] = MessageId.ToString("D");

        Assert.Equal(EventStructuralLockResult.Applied, await Service.ConsumeAsync(body.ToJsonString(), CancellationToken.None));

        Assert.Equal(MessageId, Assert.Single(locks.Claims).CorrelationId);
        Assert.Equal(MessageId, Assert.IsType<AuditEntryEntity>(events.Audit).CorrelationId);
    }

    [Theory]
    [InlineData("scheduled", "flexible")]
    [InlineData("scheduled", "scheduled_locked")]
    [InlineData("immediate", "immediate_once")]
    public async Task KeepsTheCurrentOpeningPolicyWhenLockingAsync(string mode, string state)
    {
        var item = events.Item!;
        item.OpeningMode = mode;
        item.AccessState = state;
        item.OpensAt = mode == "scheduled" ? new DateTime(2031, 1, 9) : null;
        item.AccessFrozenAt = state == "flexible" ? null : new DateTime(2031, 1, 2, 12, 0, 0);
        var policy = (item.OpeningMode, item.AccessState, item.OpensAt, item.AccessFrozenAt, item.AccessVersion);
        var window = (item.StartsAt, item.SubmissionOpensAt, item.SubmissionClosesAt, item.CompletionDeadlineAt);

        Assert.Equal(EventStructuralLockResult.Applied, await Service.ConsumeAsync(Envelope(), CancellationToken.None));

        Assert.Equal(policy, (item.OpeningMode, item.AccessState, item.OpensAt, item.AccessFrozenAt, item.AccessVersion));
        Assert.Equal(window, (item.StartsAt, item.SubmissionOpensAt, item.SubmissionClosesAt, item.CompletionDeadlineAt));
    }

    [Fact]
    public async Task KeepsAnAcceptedInvitationAndExistingMembershipAsync()
    {
        var invitation = Invitation("accepted");
        invitation.RecipientAccountId = 20;
        invitation.AcceptedAt = ConfirmedAt.AddMinutes(-1);
        invitations.Items.Add(invitation);
        invitations.MemberCount = 2;

        Assert.Equal(EventStructuralLockResult.Applied, await Service.ConsumeAsync(Envelope(), CancellationToken.None));

        Assert.Empty(invitations.Closed);
        Assert.Equal("accepted", invitation.State);
        Assert.Equal(20, invitation.RecipientAccountId);
        Assert.Equal(ConfirmedAt.AddMinutes(-1), invitation.AcceptedAt);
        Assert.Null(invitation.InvalidatedAt);
        Assert.Equal(5, invitation.Version);
        Assert.Equal(2, invitations.MemberCount);
        Assert.Null(invitations.NewMember);
    }

    [Fact]
    public async Task RepeatsNeitherTheLockNorAuditForEquivalentDeliveriesAsync()
    {
        invitations.Items.Add(Invitation("pending"));
        Assert.Equal(EventStructuralLockResult.Applied, await Service.ConsumeAsync(Envelope(), CancellationToken.None));
        var audit = events.Audit;
        clock.Now = clock.Now.AddSeconds(20);
        var replay = JsonNode.Parse(Envelope())!.AsObject();
        replay["occurredAt"] = "2031-01-01T00:00:00.120+00:00";
        replay["data"]!["optionalMetadata"] = "optional";

        Assert.Equal(EventStructuralLockResult.Duplicate, await Service.ConsumeAsync(replay.ToJsonString(), CancellationToken.None));
        Assert.Equal(EventStructuralLockResult.Rejected, await Service.ConsumeAsync(Envelope("238"), CancellationToken.None));

        Assert.Single(locks.Applications);
        Assert.Single(locks.Completions);
        Assert.Single(invitations.Closed);
        Assert.Same(audit, events.Audit);
    }

    [Theory]
    [InlineData(237, EventStructuralLockResult.Duplicate, true)]
    [InlineData(238, EventStructuralLockResult.Rejected, false)]
    public async Task PreservesTheFirstLockWhenTheDeliveryHasNoDedupeEntryAsync(long firstRecordId,
        EventStructuralLockResult expected, bool complete)
    {
        var firstLock = ConfirmedAt.AddSeconds(1).UtcDateTime;
        events.Item = Event(firstLock, firstRecordId);
        invitations.Items.Add(Invitation("pending"));

        Assert.Equal(expected, await Service.ConsumeAsync(Envelope(), CancellationToken.None));

        Assert.Equal(firstLock, events.Item.LockedAt);
        Assert.Equal(firstRecordId, events.Item.FirstRecordId);
        Assert.Equal(ConfirmedInBrasilia, events.Item.FirstRecordConfirmedAt);
        Assert.Equal(7, events.Item.Version);
        Assert.Empty(locks.Applications);
        Assert.Empty(invitations.Closed);
        Assert.Null(events.Audit);
        Assert.Equal(complete, events.Transaction.Committed);
        Assert.Equal(complete ? 1 : 0, locks.Completions.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("type")]
    [InlineData("version")]
    [InlineData("event")]
    [InlineData("record")]
    [InlineData("confirmation")]
    [InlineData("correlation")]
    public async Task RejectsUnverifiedOutboxEvidenceWithoutEffectsAsync(string discrepancy)
    {
        var origin = Origin();
        outbox.Origin = discrepancy switch
        {
            "type" => origin with { Type = "record.media-confirmed.v1" },
            "version" => origin with { ProtocolVersion = 2 },
            "event" => origin with { EventId = 43 },
            "record" => origin with { Payload = OriginPayload("238", ConfirmedAt) },
            "confirmation" => origin with { Payload = OriginPayload("237", ConfirmedAt.AddSeconds(1)) },
            "correlation" => origin with { CorrelationId = Guid.NewGuid() },
            _ => null
        };

        Assert.Equal(EventStructuralLockResult.Rejected, await Service.ConsumeAsync(Envelope(), CancellationToken.None));

        Assert.Empty(locks.Claims);
        AssertNoEffects();
    }

    [Theory]
    [InlineData("messageId", "797e003a-e78b-5ade-a746-de13475a04f1")]
    [InlineData("eventId", "43")]
    [InlineData("occurredAt", "2031-01-01T00:00:01Z")]
    [InlineData("correlationId", "2d96b933-b56c-4cdb-96b3-a28749ded0e9")]
    public async Task RejectsAConflictingDeliveryWithoutEffectsAsync(string field, string value)
    {
        var body = JsonNode.Parse(Envelope())!.AsObject();
        body[field] = value;

        Assert.Equal(EventStructuralLockResult.Rejected, await Service.ConsumeAsync(body.ToJsonString(), CancellationToken.None));

        AssertNoEffects();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresTheRecordConfirmationInTheSameEventAsync(bool hasDifferentTime)
    {
        locks.ConfirmedAt = hasDifferentTime ? ConfirmedInBrasilia.AddSeconds(1) : null;

        Assert.Equal(EventStructuralLockResult.Rejected, await Service.ConsumeAsync(Envelope(), CancellationToken.None));

        Assert.Equal((42L, 237L, CancellationToken.None), Assert.Single(locks.RecordLookups));
        AssertNoEffects();
        Assert.True(events.Transaction.Disposed);
    }

    [Fact]
    public async Task PreservesAnExistingDedupeEntryWithADifferentHashAsync()
    {
        var previous = Enumerable.Repeat((byte)42, 32).ToArray();
        var snapshot = previous.ToArray();
        locks.PriorHash = previous;

        Assert.Equal(EventStructuralLockResult.Rejected, await Service.ConsumeAsync(Envelope(), CancellationToken.None));

        Assert.Equal(snapshot, previous);
        Assert.Single(locks.Claims);
        Assert.Empty(locks.RecordLookups);
        AssertNoEffects();
    }

    [Fact]
    public async Task DisposesTheTransactionWithoutCommittingWhenAuditFailsAsync()
    {
        invitations.Items.Add(Invitation("pending"));
        events.FailAudit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ConsumeAsync(Envelope(), CancellationToken.None));

        Assert.Single(locks.Applications);
        Assert.Single(invitations.Closed);
        Assert.Empty(locks.Completions);
        Assert.False(events.Transaction.Committed);
        Assert.True(events.Transaction.Disposed);
    }

    [Fact]
    public async Task PropagatesCancellationBeforeAnEffectCanBeCommittedAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ConsumeAsync(Envelope(), cancellation.Token));

        Assert.Empty(locks.Claims);
        AssertNoEffects();
    }

    private void AssertNoEffects()
    {
        Assert.Empty(locks.Applications);
        Assert.Empty(locks.Completions);
        Assert.Empty(invitations.Closed);
        Assert.Null(events.Audit);
        Assert.False(events.Transaction.Committed);
    }

    private static string Envelope(string recordId = "237") => JsonSerializer.Serialize(new
    {
        messageId = MessageId,
        type = FirstConfirmationType,
        version = 1,
        eventId = "42",
        occurredAt = "2031-01-01T00:00:00.120Z",
        correlationId = CorrelationId,
        data = new { recordId, confirmedAt = "2031-01-01T00:00:00.120Z" }
    });

    private static OutboxMessageEntity Origin() => new(MessageId, FirstConfirmationType, 1, "event", 42,
        OriginPayload("237", ConfirmedAt), CorrelationId, ConfirmedAt, 1, null);

    private static string OriginPayload(string recordId, DateTimeOffset confirmedAt) =>
        JsonSerializer.Serialize(new { eventId = "42", recordId, confirmedAt });

    private static EventEntity Event(DateTime? lockedAt = null, long? firstRecordId = null) => new()
    {
        Id = 42,
        Version = 7,
        Code = new string('A', 43),
        Name = "Evento sintético",
        StartsAt = new DateTime(2031, 1, 1, 18, 30, 0),
        SubmissionOpensAt = new DateTime(2031, 1, 1, 16, 30, 0),
        SubmissionClosesAt = new DateTime(2031, 1, 2, 12, 0, 0),
        CompletionDeadlineAt = new DateTime(2031, 1, 2, 13, 0, 0),
        OpeningMode = "scheduled",
        AccessState = "flexible",
        AccessVersion = 13,
        OpensAt = new DateTime(2031, 1, 9),
        LockedAt = lockedAt,
        FirstRecordId = firstRecordId,
        FirstRecordConfirmedAt = lockedAt.HasValue ? ConfirmedInBrasilia : null
    };

    private static InvitationEntity Invitation(string state) => new()
    {
        Id = 12,
        EventId = 42,
        IssuerMemberId = 16,
        HashKeyId = "hash-key",
        TokenHash = [1, 2, 3],
        EncryptionKeyId = state == "pending" ? "cipher-key" : null,
        TokenCiphertext = state == "pending" ? [4, 5, 6] : null,
        State = state,
        Version = 5,
        ExpiresAt = ConfirmedAt.AddHours(1)
    };
}
