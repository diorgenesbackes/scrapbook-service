using Npgsql;

namespace ScrapBook.Api.Options;

public sealed class DatabaseOptions
{
    private DatabaseOptions(string connectionString, TimeSpan readinessTimeout)
    {
        ConnectionString = connectionString;
        ReadinessTimeout = readinessTimeout;
    }

    public string ConnectionString { get; }
    public TimeSpan ReadinessTimeout { get; }

    public static DatabaseOptions FromConfiguration(IConfiguration configuration)
    {
        var source = configuration.GetConnectionString("ScrapBook");
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new StartupConfigurationException("ConnectionStrings:ScrapBook");
        }

        var settings = ParseConnection(source);
        var timeoutValue = configuration["Database:ReadinessTimeoutMs"] ?? "2000";
        if (!int.TryParse(timeoutValue, out var timeout) || timeout is < 100 or > 10000)
        {
            throw new StartupConfigurationException("Database:ReadinessTimeoutMs");
        }

        return new DatabaseOptions(settings.ConnectionString, TimeSpan.FromMilliseconds(timeout));
    }

    private static NpgsqlConnectionStringBuilder ParseConnection(string value)
    {
        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            throw new StartupConfigurationException("ConnectionStrings:ScrapBook");
        }

        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database)
            || settings.Username != "scrapbook_app" || string.IsNullOrWhiteSpace(settings.Password)
            || settings.IncludeErrorDetail || settings.LogParameters)
        {
            throw new StartupConfigurationException("ConnectionStrings:ScrapBook");
        }

        return settings;
    }
}
