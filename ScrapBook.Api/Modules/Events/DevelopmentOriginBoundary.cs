using ScrapBook.Api.Modules.Identity;
using ScrapBook.Application.Modules.Events;

namespace ScrapBook.Api.Modules.Events;

public static class DevelopmentOriginBoundary
{
    public static void Validate(HttpContext context, DevelopmentIdentityOptions options)
    {
        if (options.Enabled && context.Request.Method is "POST" or "PUT"
            && context.Request.Headers.TryGetValue("Origin", out var origins)
            && (origins.Count != 1 || !options.AllowsOrigin(origins[0] ?? "")))
            throw new EventFailure(EventFailureKind.OriginNotAllowed);
    }
}
