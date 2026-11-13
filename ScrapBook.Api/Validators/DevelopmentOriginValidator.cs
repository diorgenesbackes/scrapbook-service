using ScrapBook.Api.Options;
using ScrapBook.Domain.Enums;
using ScrapBook.Application.Exceptions;
namespace ScrapBook.Api.Validators;

public static class DevelopmentOriginValidator
{
    public static void Validate(HttpContext context, DevelopmentIdentityOptions options)
    {
        if (options.Enabled && context.Request.Method is "POST" or "PUT"
            && context.Request.Headers.TryGetValue("Origin", out var origins)
            && (origins.Count != 1 || !options.AllowsOrigin(origins[0] ?? "")))
            throw new EventApplicationException(EventFailureKindEnum.OriginNotAllowed);
    }
}
