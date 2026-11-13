using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Policies;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class EventSharingService(IEventSharingRepository repository, PublicSiteModel site) : IEventSharingService
{
    public async Task<EventSharingResponse> GetAsync(CurrentActorModel actor, long eventId, CancellationToken cancellationToken)
    {
        var model = await repository.GetAsync(actor.AccountId, eventId, cancellationToken)
            ?? throw new EventApplicationException(EventFailureKindEnum.NotFound);
        var link = PublicEventLinkPolicy.Build(site.Origin, model.Code)
            ?? throw new EventApplicationException(EventFailureKindEnum.SharingUnavailable);
        return new(model.Code, model.Name, link);
    }
}
