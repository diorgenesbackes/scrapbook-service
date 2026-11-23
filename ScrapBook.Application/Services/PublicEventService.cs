using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class PublicEventService(IEventRepository repository, TimeProvider clock)
{
    public async Task<PublicEventResponse> GetAsync(string? code, CancellationToken cancellationToken)
    {
        if (code is not { Length: 43 } || !code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new PublicEventNotFoundException();
        }

        var item = await repository.FindByCodeAsync(code, cancellationToken) ?? throw new PublicEventNotFoundException();
        var now = clock.GetUtcNow().ToUniversalTime();
        var window = EventSubmissionWindowService.Describe(item.SubmissionOpensAt, item.SubmissionClosesAt, item.CompletionDeadlineAt, now);
        return new PublicEventResponse(item.Name, EventService.FormatCivil(item.StartsAt), EventOpeningService.Brasilia,
            EventService.DefaultCover, item.WelcomeMessage ?? EventService.Welcome, window, now);
    }
}
