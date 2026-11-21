using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Api.Readers;

public static class HttpJsonBodyReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    public static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
            || !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || media.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new EventJsonMediaTypeException();
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, Options, cancellationToken)
                ?? throw new EventRequestValidationException("body");
        }
        catch (JsonException)
        {
            throw new EventRequestValidationException("body");
        }
    }
}
