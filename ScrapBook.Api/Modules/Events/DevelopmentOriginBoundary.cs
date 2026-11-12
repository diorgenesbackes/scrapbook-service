using ScrapBook.Api.Modules.Identity;

namespace ScrapBook.Api.Modules.Events;

public static class DevelopmentOriginBoundary
{
    public static IApplicationBuilder UseDevelopmentOriginBoundary(this IApplicationBuilder application) =>
        application.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<DevelopmentIdentityOptions>();
            if (options.Enabled && context.Request.Path.StartsWithSegments("/api/v1/events")
                && context.Request.Method is "POST" or "PUT"
                && context.Request.Headers.TryGetValue("Origin", out var origins)
                && (origins.Count != 1 || !options.AllowsOrigin(origins[0] ?? "")))
            {
                var correlation = Guid.TryParse(context.Request.Headers["X-Correlation-Id"], out var supplied)
                    ? supplied : Guid.NewGuid();
                context.Response.Headers["X-Correlation-Id"] = correlation.ToString();
                context.Response.Headers.CacheControl = "no-store";
                await Results.Problem(type: "about:blank", title: "Forbidden", statusCode: 403,
                    detail: "Origem da requisição não permitida.", extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "REQUEST_ORIGIN_NOT_ALLOWED", ["correlationId"] = correlation.ToString()
                    }).ExecuteAsync(context);
                return;
            }

            await next();
        });
}
