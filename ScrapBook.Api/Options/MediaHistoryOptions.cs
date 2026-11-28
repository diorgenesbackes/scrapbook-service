using System.Globalization;

namespace ScrapBook.Api.Options;

public static class MediaHistoryOptions
{
    public static DateTimeOffset? ReadCutoff(IConfiguration configuration)
    {
        const string key = "MediaRecovery:HistoryBeforeUtc";
        var value = configuration[key];
        if (value is null) return null;
        if (!(value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal))
            || !DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var cutoff) || cutoff.Offset != TimeSpan.Zero || cutoff > TimeProvider.System.GetUtcNow())
        {
            throw new StartupConfigurationException(key);
        }
        return cutoff.AddTicks(-(cutoff.UtcTicks % TimeSpan.TicksPerMicrosecond));
    }
}
