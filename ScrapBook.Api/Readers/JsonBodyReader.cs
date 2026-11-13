using ScrapBook.Api.Validators;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ScrapBook.Api.Readers;

public static class JsonBodyReader
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static async Task<T> ReadAsync<T>(HttpContext context) where T : class
        => (T)await ReadAsync(context, typeof(T));

    public static async Task<object> ReadAsync(HttpContext context, Type modelType)
    {
        EventRequestValidator.ValidateBodyHeaders(context);
        try
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, modelType, InputJson, context.RequestAborted)
                ?? throw EventRequestValidator.Invalid("body", "Informe um objeto JSON válido.");
        }
        catch (JsonException) { throw EventRequestValidator.Invalid("body", "Revise os campos obrigatórios, tipos e campos permitidos do JSON."); }
    }

}
