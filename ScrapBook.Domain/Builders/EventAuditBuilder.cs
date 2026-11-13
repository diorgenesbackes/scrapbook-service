using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Entities;
namespace ScrapBook.Domain.Builders;

public static class EventAuditBuilder
{
    private static Dictionary<string, object?> Fields(EventEntity state) => new()
    {
        ["starts_at"] = state.StartsAt,
        ["submission_closes_at"] = state.SubmissionClosesAt,
        ["opening_mode"] = state.OpeningMode,
        ["opens_at"] = state.OpensAt,
        ["access_state"] = state.AccessState,
        ["access_version"] = state.AccessVersion
    };

    public static EventAuditModel Created(long actor, EventEntity state, Guid correlation) =>
        new(actor, state.Id, "events.created", correlation, new Dictionary<string, object?>(), Fields(state), "configuration_created");

    public static EventAuditModel Updated(long actor, EventEntity before, EventEntity after,
        EventMutationEnum mutation, Guid correlation) => new(actor, after.Id,
            mutation == EventMutationEnum.Structure ? "events.structure_updated" : "events.access_updated",
            correlation, Fields(before), Fields(after), "configuration_changed");
}
