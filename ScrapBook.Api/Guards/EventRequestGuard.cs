using System.Globalization;
using System.Security.Claims;
using ScrapBook.Api.Options;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Api.Guards;

public static class EventRequestGuard
{
    public static long RequireAccount(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.User.Identity?.IsAuthenticated != true
            || !long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None,
                CultureInfo.InvariantCulture, out var accountId) || accountId < 1)
        {
            throw new EventIdentityRequiredException();
        }

        return accountId;
    }

    public static void VerifyOrigin(HttpRequest request, PublicUiOriginOptions ui, IWebHostEnvironment environment)
    {
        var expected = ui.Origin;
        if (environment.IsDevelopment() && request.Host.Host is "127.0.0.1" or "localhost" or "[::1]")
        {
            expected ??= $"{request.Scheme}://{request.Host}";
        }

        var origins = request.Headers.Origin;
        if (expected is null || origins.Count != 1 || !string.Equals(origins[0], expected, StringComparison.Ordinal))
        {
            throw new EventOriginRejectedException();
        }
    }
}
