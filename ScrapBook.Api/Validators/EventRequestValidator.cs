using ScrapBook.Application.Enums;
using ScrapBook.Application.Exceptions;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Validators;

public static class EventRequestValidator
{
    public static void ValidateQuery(HttpContext context, params string[] allowed)
    {
        if (context.Request.Query.Any(item => !allowed.Contains(item.Key, StringComparer.Ordinal) || item.Value.Count != 1))
            throw Invalid("query", "Use somente os parâmetros documentados, uma vez cada.");
        if (context.Request.Method == "GET" && (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            throw Invalid("body", "Consultas não recebem body.");
    }
    public const long MaxBodyBytes = 32 * 1024;
    public static void ValidateBodyHeaders(HttpContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media)
            || !string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase)
            || (media.Charset.HasValue && !string.Equals(media.Charset.ToString().Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new EventApplicationException(EventFailureKindEnum.UnsupportedMediaType);
        if (context.Request.ContentLength > MaxBodyBytes) throw new EventApplicationException(EventFailureKindEnum.TooLarge);
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxBodyBytes;
    }

    public static EventApplicationException Invalid(string field, string message) => new(EventFailureKindEnum.Validation,
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
}
