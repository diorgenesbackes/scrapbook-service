using System.Net;

namespace ScrapBook.Api.Options;

public sealed class DevelopmentIdentityOptions
{
    private DevelopmentIdentityOptions(bool enabled, long accountId, string token)
    {
        Enabled = enabled;
        AccountId = accountId;
        Token = token;
    }

    public bool Enabled { get; }
    public long AccountId { get; }
    public string Token { get; }

    public static DevelopmentIdentityOptions FromConfiguration(IConfiguration configuration, bool isDevelopment, bool isContainer)
    {
        var value = configuration["DevelopmentIdentity:Enabled"] ?? "false";
        if (!bool.TryParse(value, out var enabled) || (enabled && !isDevelopment))
        {
            throw new StartupConfigurationException("DevelopmentIdentity:Enabled");
        }

        if (!enabled)
        {
            return new DevelopmentIdentityOptions(false, 0, string.Empty);
        }

        var containerBinding = configuration["DevelopmentIdentity:AllowContainerBinding"] ?? "false";
        if (!bool.TryParse(containerBinding, out var allowContainerBinding)
            || (!UsesOnlyLoopback(configuration) && !(isContainer && allowContainerBinding)))
        {
            throw new StartupConfigurationException("DevelopmentIdentity:AllowContainerBinding");
        }

        if (!long.TryParse(configuration["DevelopmentIdentity:AccountId"], out var accountId) || accountId < 1)
        {
            throw new StartupConfigurationException("DevelopmentIdentity:AccountId");
        }

        var token = configuration["DevelopmentIdentity:Token"];
        if (token is not { Length: 64 } || !token.All(Uri.IsHexDigit))
        {
            throw new StartupConfigurationException("DevelopmentIdentity:Token");
        }

        return new DevelopmentIdentityOptions(true, accountId, token);
    }

    private static bool UsesOnlyLoopback(IConfiguration configuration)
    {
        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren().ToArray();
        var urls = configuration["urls"];
        var hasPorts = !string.IsNullOrEmpty(configuration["HTTP_PORTS"])
            || !string.IsNullOrEmpty(configuration["HTTPS_PORTS"]);
        var hasHostingAddresses = !string.IsNullOrEmpty(urls) || hasPorts;
        var preferHosting = bool.TryParse(configuration["preferHostingUrls"], out var preferred) && preferred;
        if (endpoints.Length > 0 && !(preferHosting && hasHostingAddresses))
        {
            return endpoints.All(endpoint => IsLoopback(endpoint["Url"]));
        }

        if (!string.IsNullOrEmpty(urls))
        {
            return urls.Split(';', StringSplitOptions.RemoveEmptyEntries).All(IsLoopback);
        }

        return !hasPorts;
    }

    private static bool IsLoopback(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return false;
        }

        try
        {
            var binding = BindingAddress.Parse(address);
            return string.Equals(binding.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(binding.Host, out var ip) && IPAddress.IsLoopback(ip));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
