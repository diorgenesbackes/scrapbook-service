using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using ScrapBook.Application.Modules.Events;
using ScrapBook.Domain.Common;

namespace ScrapBook.Api.Modules.Events;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateEventRequest
{
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public string StartsAt { get; init; } = "";
    public string? WelcomeMessage { get; init; }
    public string OpeningMode { get; init; } = "scheduled";
    public string? OpeningDate { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEventRequest
{
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public string StartsAt { get; init; } = "";
    [JsonRequired] public string? WelcomeMessage { get; init; }
    [JsonRequired] public string ExpectedVersion { get; init; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEventAccessRequest
{
    [JsonRequired] public string OpeningMode { get; init; } = "";
    [JsonRequired] public string? OpeningDate { get; init; }
    [JsonRequired] public string ExpectedVersion { get; init; } = "";
}

public sealed record CoverDto(string Kind, string AssetKey);
public sealed record SubmissionWindowDto(string OpensAt, string ClosesAt, string CompletionDeadlineAt);
public sealed record EventDto(string Id, string Name, string StartsAt, string TimeZone, string? WelcomeMessage,
    string EffectiveWelcomeMessage, CoverDto Cover, string OpeningMode, string? OpeningDate, string? OpensAt,
    SubmissionWindowDto SubmissionWindow, string AccessState, bool CanEditStructure, bool CanEditAccess,
    string Version, string ServerTime);
public sealed record EventListItemDto(string Id, string Name, string StartsAt, CoverDto Cover, string OpeningMode,
    string? OpeningDate, string Version);
public sealed record EventPageDto(IReadOnlyList<EventListItemDto> Items, string? NextCursor, bool HasMore, string ServerTime);

public static class EventHttpContracts
{
    public const long MaxBodyBytes = 32 * 1024;
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static void ValidateBodyHeaders(HttpContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media)
            || !string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase)
            || (media.Charset.HasValue && !string.Equals(media.Charset.ToString().Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new EventFailure(EventFailureKind.UnsupportedMediaType);
        if (context.Request.ContentLength > MaxBodyBytes) throw new EventFailure(EventFailureKind.TooLarge);
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxBodyBytes;
    }

    public static async Task<T> ReadAsync<T>(HttpContext context) where T : class
    {
        ValidateBodyHeaders(context);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, InputJson, context.RequestAborted)
                ?? throw Invalid("body", "Informe um objeto JSON válido.");
        }
        catch (JsonException) { throw Invalid("body", "Revise os campos obrigatórios, tipos e campos permitidos do JSON."); }
    }

    public static EventFailure Invalid(string field, string message) => new(EventFailureKind.Validation,
        new Dictionary<string, string[]> { [field] = [message] });
    public static long Decimal(string? value, string field, bool positive)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number < 0 || (positive && number == 0))
            throw Invalid(field, "Informe uma string decimal válida dentro do intervalo bigint.");
        return number;
    }
    public static string Mode(string? value) => value is "immediate" or "scheduled" ? value
        : throw Invalid("openingMode", "Selecione abertura imediata ou programada.");
    public static string Civil(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    public static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    public static EventDto ToDto(EventDetails result)
    {
        var state = result.Configuration;
        return new(state.Id.ToString(CultureInfo.InvariantCulture), state.Name, Civil(state.StartsAt), BusinessClock.TimeZoneId,
            state.WelcomeMessage, result.EffectiveWelcomeMessage, new("default", result.CoverAssetKey), state.OpeningMode,
            Date(state.OpensAt), state.OpensAt is null ? null : Civil(state.OpensAt.Value),
            new(Civil(state.SubmissionOpensAt), Civil(state.SubmissionClosesAt), Civil(state.CompletionDeadlineAt)),
            result.AccessState, result.CanEditStructure, result.CanEditAccess, state.Version.ToString(CultureInfo.InvariantCulture), Utc(result.ServerTime));
    }
    public static EventPageDto ToDto(EventPage page) => new(page.Items.Select(state => new EventListItemDto(
        state.Id.ToString(CultureInfo.InvariantCulture), state.Name, Civil(state.StartsAt),
        new("default", ScrapBook.Domain.Modules.Events.EventRules.DefaultCoverAssetKey), state.OpeningMode,
        Date(state.OpensAt), state.Version.ToString(CultureInfo.InvariantCulture))).ToArray(), page.NextCursor, page.HasMore, Utc(page.ServerTime));
}
