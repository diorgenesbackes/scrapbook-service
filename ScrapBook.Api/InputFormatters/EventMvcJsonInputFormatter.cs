using Microsoft.AspNetCore.Mvc.Formatters;
using ScrapBook.Api.Readers;

namespace ScrapBook.Api.InputFormatters;

public sealed class EventMvcJsonInputFormatter : InputFormatter
{
    public EventMvcJsonInputFormatter() => SupportedMediaTypes.Add("application/json");

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context)
    {
        var model = await HttpJsonBodyReader.ReadAsync(context.HttpContext, context.ModelType);
        return await InputFormatterResult.SuccessAsync(model);
    }
}
