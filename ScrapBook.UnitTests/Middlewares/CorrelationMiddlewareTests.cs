using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ScrapBook.Api.Middlewares;

namespace ScrapBook.UnitTests.Middlewares;

public sealed class CorrelationMiddlewareTests
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("da771942-1b5b-4c4b-885a-62260ad1fc65", true)]
    public async Task SuppliesANonEmptyCorrelationToTheRequest(string header, bool preserve)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = header;
        var middleware = new CorrelationMiddleware(next =>
        {
            var correlation = CorrelationMiddleware.GetCorrelation(next);
            Assert.NotEqual(Guid.Empty, correlation);
            if (preserve) Assert.Equal(Guid.Parse(header), correlation);
            return Task.CompletedTask;
        }, NullLogger<CorrelationMiddleware>.Instance);

        await middleware.InvokeAsync(context);
    }
}
