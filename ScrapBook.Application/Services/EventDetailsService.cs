using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class EventDetailsService(IEventWriteRepository repository, TimeProvider clock)
{
    public async Task<EventResponse> ReplaceAsync(long accountId, string eventId, ReplaceEventDetailsRequest request,
        CancellationToken cancellationToken)
    {
        var id = EventService.ParseEventId(eventId);
        var expectedVersion = EventOpeningService.ParseVersion(request.ExpectedVersion);
        var name = EventService.ParseName(request.Name);
        var starts = EventService.ParseStartsAt(request.StartsAt);
        var welcome = EventService.ParseWelcome(request.WelcomeMessage);
        var (opens, closes, deadline) = EventService.CalculateSubmissionWindow(starts);

        await using var transaction = await repository.BeginAsync(cancellationToken);
        var item = await repository.FindLinkedForUpdateAsync(accountId, id, cancellationToken)
            ?? throw new EventNotFoundException();

        if (item.Version != expectedVersion)
        {
            throw new EventVersionConflictException();
        }

        if (item.LockedAt is not null)
        {
            throw new EventStructuralLockedException();
        }

        var now = clock.GetUtcNow();
        if (item.Name == name && item.StartsAt == starts && item.WelcomeMessage == welcome)
        {
            return EventService.Represent(item, now);
        }

        if (item.OpensAt <= closes)
        {
            throw new EventRequestValidationException("startsAt");
        }

        EventOpeningService.PreserveFreeze(item, TimeZoneInfo.ConvertTime(now, EventOpeningService.Zone).DateTime, closes);
        item.Name = name;
        item.StartsAt = starts;
        item.WelcomeMessage = welcome;
        item.SubmissionOpensAt = opens;
        item.SubmissionClosesAt = closes;
        item.CompletionDeadlineAt = deadline;
        item.Version++;
        await repository.SaveDetailsAsync(item, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EventService.Represent(item, now);
    }
}
