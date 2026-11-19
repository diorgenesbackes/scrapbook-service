using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ScrapBook.Api.Options;

namespace ScrapBook.Api.AuthenticationHandlers;

public sealed class DevelopmentIdentityAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DevelopmentIdentityOptions identity) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Headers.Authorization;
        if (!identity.Enabled || values.Count != 1
            || !AuthenticationHeaderValue.TryParse(values[0], out var header)
            || !string.Equals(header.Scheme, SchemeName, StringComparison.OrdinalIgnoreCase)
            || header.Parameter is not { Length: 64 } token || !token.All(Uri.IsHexDigit))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(token), Convert.FromHexString(identity.Token)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Identidade de desenvolvimento inválida."));
        }

        var claim = new Claim(ClaimTypes.NameIdentifier, identity.AccountId.ToString(CultureInfo.InvariantCulture));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([claim], SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
