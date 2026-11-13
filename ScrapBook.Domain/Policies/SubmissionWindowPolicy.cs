using ScrapBook.Domain.Clocks;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Policies;

public static class SubmissionWindowPolicy
{
    public const int MaximumRevalidationMs = 30_000;

    public static PublicSubmissionWindowModel Evaluate(DateTime startsAt, DateTimeOffset serverTime)
    {
        var window = EventPolicy.Window(startsAt);
        var now = BusinessClock.ToCivil(serverTime);
        var state = now < window.OpensAt ? SubmissionWindowStateEnum.NotOpen
            : now < window.ClosesAt ? SubmissionWindowStateEnum.Open : SubmissionWindowStateEnum.Closed;
        var nextBoundary = state == SubmissionWindowStateEnum.NotOpen ? window.OpensAt
            : state == SubmissionWindowStateEnum.Open ? window.ClosesAt : (DateTime?)null;
        // Arredondar para baixo evita anunciar disponibilidade além da borda.
        var revalidateAfterMs = nextBoundary is null ? MaximumRevalidationMs
            : (int)Math.Clamp(Math.Floor((nextBoundary.Value - now).TotalMilliseconds), 0, MaximumRevalidationMs);
        return new(state, window.OpensAt, window.ClosesAt, revalidateAfterMs);
    }
}
