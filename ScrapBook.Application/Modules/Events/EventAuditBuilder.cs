using ScrapBook.Domain.Modules.Events;

namespace ScrapBook.Application.Modules.Events;

public sealed record EventAudit(long AccountId, long EventId, string Action, Guid CorrelationId,
    IReadOnlyDictionary<string, object?> Before, IReadOnlyDictionary<string, object?> After, string ReasonCode);

public static class EventAuditBuilder
{
    private static Dictionary<string, object?> Fields(EventConfiguration state) => new()
    {
        ["starts_at"] = state.StartsAt, ["submission_closes_at"] = state.SubmissionClosesAt,
        ["opening_mode"] = state.OpeningMode, ["opens_at"] = state.OpensAt,
        ["access_state"] = state.AccessState, ["access_version"] = state.AccessVersion
    };

    public static EventAudit Created(long actor, EventConfiguration state, Guid correlation) =>
        new(actor, state.Id, "events.created", correlation, new Dictionary<string, object?>(), Fields(state), "configuration_created");

    public static EventAudit Updated(long actor, EventConfiguration before, EventConfiguration after,
        EventMutation mutation, Guid correlation) => new(actor, after.Id,
            mutation == EventMutation.Structure ? "events.structure_updated" : "events.access_updated",
            correlation, Fields(before), Fields(after), "configuration_changed");
}
