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

    public static DatabaseOptions FromConfiguration(IConfiguration configuration) =>
        FromConfiguration(configuration, "ScrapBook", false);

    public static DatabaseOptions ForMediaRecovery(IConfiguration configuration) =>
        FromConfiguration(configuration, "ScrapBookMediaRecovery", true);

    private static DatabaseOptions FromConfiguration(IConfiguration configuration, string connectionName, bool mediaRecovery)
    {
        var path = $"ConnectionStrings:{connectionName}";
        var source = configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new StartupConfigurationException(path);
        }

        var settings = ParseConnection(source, path, mediaRecovery);
        if (!bool.TryParse(configuration["Database:RequireTls"] ?? "false", out var requireTls))
        {
            throw new StartupConfigurationException("Database:RequireTls");
        }

        if (requireTls && (settings.SslMode != SslMode.VerifyFull
            || settings.RootCertificate != "/app/certs/rds-us-east-1.pem"))
        {
            throw new StartupConfigurationException(path);
        }

        var timeoutValue = configuration["Database:ReadinessTimeoutMs"] ?? "2000";
        if (!int.TryParse(timeoutValue, out var timeout) || timeout is < 100 or > 10000)
        {
            throw new StartupConfigurationException("Database:ReadinessTimeoutMs");
        }

        return new DatabaseOptions(settings.ConnectionString, TimeSpan.FromMilliseconds(timeout));
    }

    private static NpgsqlConnectionStringBuilder ParseConnection(string value, string path, bool mediaRecovery)
    {
        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            throw new StartupConfigurationException(path);
        }

        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database)
            || string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password)
            || (mediaRecovery ? settings.Username is "scrapbook_app" or "scrapbook_migrator" : settings.Username != "scrapbook_app")
            || settings.IncludeErrorDetail || settings.LogParameters)
        {
            throw new StartupConfigurationException(path);
        }

        if (settings.Pooling && !settings.ShouldSerialize("Maximum Pool Size"))
        {
            settings.MaxPoolSize = Math.Max(settings.MinPoolSize, 32);
        }

        return settings;
    }
}
