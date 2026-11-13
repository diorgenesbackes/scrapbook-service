using ScrapBook.Domain.Entities;
namespace ScrapBook.Infrastructure.Entities;

internal sealed class EventPersistenceEntity
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

    public static EventPersistenceEntity FromSnapshot(EventEntity state) => new()
    {
        CreatorId = state.CreatorId,
        Code = state.Code,
        Name = state.Name,
        WelcomeMessage = state.WelcomeMessage,
        StartsAt = state.StartsAt,
        SubmissionOpensAt = state.SubmissionOpensAt,
        SubmissionClosesAt = state.SubmissionClosesAt,
        CompletionDeadlineAt = state.CompletionDeadlineAt,
        OpeningMode = state.OpeningMode,
        OpensAt = state.OpensAt,
        AccessState = state.AccessState,
        AccessFrozenAt = state.AccessFrozenAt
    };

    public void Apply(EventEntity state)
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

    public EventEntity Snapshot() => new()
    {
        Id = Id,
        CreatorId = CreatorId,
        CoverId = CoverId,
        Code = Code,
        Name = Name,
        WelcomeMessage = WelcomeMessage,
        StartsAt = StartsAt,
        SubmissionOpensAt = SubmissionOpensAt,
        SubmissionClosesAt = SubmissionClosesAt,
        CompletionDeadlineAt = CompletionDeadlineAt,
        OpeningMode = OpeningMode,
        OpensAt = OpensAt,
        AccessState = AccessState,
        AccessFrozenAt = AccessFrozenAt,
        AccessVersion = AccessVersion,
        LockedAt = LockedAt,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Version = Version
    };
}
