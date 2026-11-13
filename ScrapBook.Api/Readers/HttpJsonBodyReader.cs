using ScrapBook.Api.Validators;
using ScrapBook.Domain.Readers;
using System.Text.Json;

namespace ScrapBook.Api.Readers;

public static class HttpJsonBodyReader
{
    public static async Task<T> ReadAsync<T>(HttpContext context) where T : class
        => (T)await ReadAsync(context, typeof(T));

    public static async Task<object> ReadAsync(HttpContext context, Type modelType)
    {
        EventRequestValidator.ValidateBodyHeaders(context);
        try
        {
            return await JsonBodyReader.ReadAsync(context.Request.Body, modelType, context.RequestAborted)
                ?? throw EventRequestValidator.Invalid("body", "Informe um objeto JSON válido.");
        }
        catch (JsonException) { throw EventRequestValidator.Invalid("body", "Revise os campos obrigatórios, tipos e campos permitidos do JSON."); }
    }
}
