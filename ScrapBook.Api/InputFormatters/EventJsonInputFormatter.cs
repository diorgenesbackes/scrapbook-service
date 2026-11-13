using Microsoft.AspNetCore.Mvc.Formatters;
using ScrapBook.Api.Readers;

namespace ScrapBook.Api.InputFormatters;

public sealed class EventJsonInputFormatter : InputFormatter
{
    public EventJsonInputFormatter() => SupportedMediaTypes.Add("application/json");

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context)
    {
        var model = await JsonBodyReader.ReadAsync(context.HttpContext, context.ModelType);
        return await InputFormatterResult.SuccessAsync(model);
    }
}
