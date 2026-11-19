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

    public static DevelopmentIdentityOptions FromConfiguration(IConfiguration configuration, bool isDevelopment)
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
}
