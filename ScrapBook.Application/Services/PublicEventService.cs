using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Policies;
using ScrapBook.Domain.Clocks;

namespace ScrapBook.Application.Services;

public sealed class PublicEventService(IPublicEventRepository repository, PublicSiteModel site, BusinessClock clock) : IPublicEventService
{
    public async Task<PublicEventPresentationModel> GetAsync(string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PublicEventCodePolicy.IsValid(code)) throw new EventApplicationException(EventFailureKindEnum.NotFound);
        var model = await repository.GetAsync(code, cancellationToken)
            ?? throw new EventApplicationException(EventFailureKindEnum.NotFound);
        var serverTime = clock.UtcNow;
        return new(model.Code, model.Name, EventPolicy.EffectiveWelcomeMessage(model.WelcomeMessage), EventPolicy.DefaultCoverAssetKey,
            PublicEventLinkPolicy.Build(site.Origin, model.Code), serverTime, SubmissionWindowPolicy.Evaluate(model.StartsAt, serverTime));
    }
}
