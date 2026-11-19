using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScrapBook.Api.AuthenticationHandlers;
using ScrapBook.Api.Options;

namespace ScrapBook.UnitTests.AuthenticationHandlers;

public sealed class DevelopmentIdentityAuthenticationHandlerTests
{
    [Theory]
    [InlineData(false, "a", false, false)]
    [InlineData(true, "a", true, false)]
    [InlineData(true, "b", false, true)]
    [InlineData(true, "x", false, false)]
    public async Task AcceptsOnlyAnExplicitValidDevelopmentTokenAsync(bool enabled, string tokenCharacter,
        bool authenticated, bool failed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DevelopmentIdentity:Enabled"] = enabled.ToString(),
            ["DevelopmentIdentity:AccountId"] = "42",
            ["DevelopmentIdentity:Token"] = new string('a', 64)
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(DevelopmentIdentityOptions.FromConfiguration(config, true));
        services.AddAuthentication(DevelopmentIdentityAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DevelopmentIdentityAuthenticationHandler>(
                DevelopmentIdentityAuthenticationHandler.SchemeName, _ => { });
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = "Development " + string.Concat(Enumerable.Repeat(tokenCharacter, 64));
        var result = await context.AuthenticateAsync();

        Assert.Equal(authenticated, result.Succeeded);
        Assert.Equal(failed, result.Failure is not null);
        Assert.Equal(authenticated ? "42" : null, result.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
    }
}
