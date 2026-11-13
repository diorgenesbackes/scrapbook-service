using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Policies;

namespace ScrapBook.Application.Services;

public sealed class PublicEventService(IPublicEventRepository repository) : IPublicEventService
{
    public async Task<PublicEventPresentationModel> GetAsync(string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PublicEventCodePolicy.IsValid(code)) throw new EventApplicationException(EventFailureKindEnum.NotFound);
        var model = await repository.GetAsync(code, cancellationToken)
            ?? throw new EventApplicationException(EventFailureKindEnum.NotFound);
        return new(model.Code, model.Name, EventPolicy.EffectiveWelcomeMessage(model.WelcomeMessage), EventPolicy.DefaultCoverAssetKey);
    }
}
