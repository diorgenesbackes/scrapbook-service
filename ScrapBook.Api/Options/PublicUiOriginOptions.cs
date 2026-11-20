namespace ScrapBook.Api.Options;

public sealed class PublicUiOriginOptions
{
    public const string ConfigurationKey = "Hosting:PublicUiOrigin";
    public const string PolicyName = "public-ui";

    private PublicUiOriginOptions(string? origin)
    {
        Origin = origin;
    }

    public string? Origin { get; }

    public static PublicUiOriginOptions FromConfiguration(IConfiguration configuration)
    {
        var value = configuration[ConfigurationKey];
        if (value is null)
        {
            return new PublicUiOriginOptions(null);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.HostNameType != UriHostNameType.Dns
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || value != uri.GetLeftPart(UriPartial.Authority))
        {
            throw new StartupConfigurationException(ConfigurationKey);
        }

        return new PublicUiOriginOptions(value);
    }
}
