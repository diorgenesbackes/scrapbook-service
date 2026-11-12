using ScrapBook.Domain.Modules.Events;

namespace ScrapBook.Infrastructure.Modules.Events.Persistence;

internal sealed class EventRow
{
    public long Id { get; set; }
    public long CreatorId { get; set; }
    public long? CoverId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? WelcomeMessage { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime SubmissionOpensAt { get; set; }
    public DateTime SubmissionClosesAt { get; set; }
    public DateTime CompletionDeadlineAt { get; set; }
    public string OpeningMode { get; set; } = "scheduled";
    public DateTime? OpensAt { get; set; }
    public string AccessState { get; set; } = "flexible";
    public DateTime? AccessFrozenAt { get; set; }
    public long AccessVersion { get; set; } = 1;
    public DateTimeOffset? LockedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }

    public static EventRow FromSnapshot(EventConfiguration state) => new()
    {
        CreatorId = state.CreatorId, Code = state.Code, Name = state.Name, WelcomeMessage = state.WelcomeMessage,
        StartsAt = state.StartsAt, SubmissionOpensAt = state.SubmissionOpensAt,
        SubmissionClosesAt = state.SubmissionClosesAt, CompletionDeadlineAt = state.CompletionDeadlineAt,
        OpeningMode = state.OpeningMode, OpensAt = state.OpensAt, AccessState = state.AccessState,
        AccessFrozenAt = state.AccessFrozenAt
    };

    public void Apply(EventConfiguration state)
    {
        Name = state.Name;
        WelcomeMessage = state.WelcomeMessage;
        StartsAt = state.StartsAt;
        SubmissionOpensAt = state.SubmissionOpensAt;
        SubmissionClosesAt = state.SubmissionClosesAt;
        CompletionDeadlineAt = state.CompletionDeadlineAt;
        OpeningMode = state.OpeningMode;
        OpensAt = state.OpensAt;
        AccessState = state.AccessState;
        AccessFrozenAt = state.AccessFrozenAt;
    }

    public EventConfiguration Snapshot() => new()
    {
        Id = Id, CreatorId = CreatorId, CoverId = CoverId, Code = Code, Name = Name,
        WelcomeMessage = WelcomeMessage, StartsAt = StartsAt, SubmissionOpensAt = SubmissionOpensAt,
        SubmissionClosesAt = SubmissionClosesAt, CompletionDeadlineAt = CompletionDeadlineAt,
        OpeningMode = OpeningMode, OpensAt = OpensAt, AccessState = AccessState,
        AccessFrozenAt = AccessFrozenAt, AccessVersion = AccessVersion, LockedAt = LockedAt,
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, Version = Version
    };
}

internal sealed class EventMemberRow
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long AccountId { get; set; }
    public short Slot { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class EventAuditRow
{
    public long Id { get; set; }
    public long? AccountId { get; set; }
    public long? EventId { get; set; }
    public string ActorType { get; set; } = "account";
    public string Action { get; set; } = "";
    public string SubjectType { get; set; } = "event";
    public long? SubjectId { get; set; }
    public string Outcome { get; set; } = "success";
    public string? ReasonCode { get; set; }
    public Guid CorrelationId { get; set; }
    public string BeforeState { get; set; } = "{}";
    public string AfterState { get; set; } = "{}";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class OpeningDispatchRow
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long MemberId { get; set; }
    public long AccessVersion { get; set; }
    public DateTime DueAt { get; set; }
    public string State { get; set; } = "pending";
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}
