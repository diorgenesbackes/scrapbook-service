using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Domain.Mappers;

public static class PublicEventResponseMapper
{
    public static PublicEventResponse ToResponse(PublicEventPresentationModel model) =>
        new(model.Code, model.Name, model.EffectiveWelcomeMessage, new("default", model.CoverAssetKey));
}
