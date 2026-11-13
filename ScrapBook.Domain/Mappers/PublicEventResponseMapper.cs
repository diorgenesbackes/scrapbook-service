using ScrapBook.Domain.Models;
using ScrapBook.Domain.Responses;
using ScrapBook.Domain.Clocks;
using ScrapBook.Domain.Enums;

namespace ScrapBook.Domain.Mappers;

public static class PublicEventResponseMapper
{
    public static PublicEventResponse ToResponse(PublicEventPresentationModel model) =>
        new(model.Code, model.Name, model.EffectiveWelcomeMessage, new("default", model.CoverAssetKey), model.PublicUrl,
            EventResponseMapper.Utc(model.ServerTime), new(model.SubmissionWindow.State switch
            {
                SubmissionWindowStateEnum.NotOpen => "not_open",
                SubmissionWindowStateEnum.Open => "open",
                SubmissionWindowStateEnum.Closed => "closed",
                _ => throw new ArgumentOutOfRangeException(nameof(model))
            }, EventResponseMapper.Civil(model.SubmissionWindow.OpensAt), EventResponseMapper.Civil(model.SubmissionWindow.ClosesAt),
                BusinessClock.TimeZoneId, model.SubmissionWindow.RevalidateAfterMs));
}
