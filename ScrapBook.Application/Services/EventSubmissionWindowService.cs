using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public static class EventSubmissionWindowService
{
    public static PublicEventWindowResponse Describe(DateTime opensAt, DateTime closesAt, DateTime completionDeadlineAt,
        DateTimeOffset now)
    {
        var opening = EventOpeningService.ToBrasiliaInstant(opensAt);
        var closing = EventOpeningService.ToBrasiliaInstant(closesAt);
        var (state, transition) = now < opening ? ("before", (DateTimeOffset?)opening)
            : now < closing ? ("open", closing) : ("closed", null);

        return new PublicEventWindowResponse(state, EventService.FormatCivil(opensAt), EventService.FormatCivil(closesAt),
            EventService.FormatCivil(completionDeadlineAt), transition);
    }
}
