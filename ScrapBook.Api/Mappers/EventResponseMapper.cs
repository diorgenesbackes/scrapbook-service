using ScrapBook.Api.Responses;
using ScrapBook.Application.Models;
using ScrapBook.Domain.Clocks;
using ScrapBook.Domain.Policies;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Mappers;

public static class EventResponseMapper
{
    public static string Civil(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    public static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    public static EventResponse ToResponse(EventDetailsModel result)
    {
        var state = result.Configuration;
        return new(state.Id.ToString(CultureInfo.InvariantCulture), state.Name, Civil(state.StartsAt), BusinessClock.TimeZoneId,
            state.WelcomeMessage, result.EffectiveWelcomeMessage, new("default", result.CoverAssetKey), state.OpeningMode,
            Date(state.OpensAt), state.OpensAt is null ? null : Civil(state.OpensAt.Value),
            new(Civil(state.SubmissionOpensAt), Civil(state.SubmissionClosesAt), Civil(state.CompletionDeadlineAt)),
            result.AccessState, result.CanEditStructure, result.CanEditAccess, state.Version.ToString(CultureInfo.InvariantCulture), Utc(result.ServerTime));
    }
    public static EventPageResponse ToResponse(EventPageModel page) => new(page.Items.Select(state => new EventListItemResponse(
        state.Id.ToString(CultureInfo.InvariantCulture), state.Name, Civil(state.StartsAt),
        new("default", ScrapBook.Domain.Policies.EventPolicy.DefaultCoverAssetKey), state.OpeningMode,
        Date(state.OpensAt), state.Version.ToString(CultureInfo.InvariantCulture))).ToArray(), page.NextCursor, page.HasMore, Utc(page.ServerTime));
}
