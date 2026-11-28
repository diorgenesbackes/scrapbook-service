using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ScrapBook.Api.Formatters;

public sealed class ScrapBookJsonConsoleFormatter(IHostEnvironment environment) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "scrapbook-json";

    private static readonly string? ServiceVersion = typeof(ScrapBookJsonConsoleFormatter).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly HashSet<string> StructuredFields =
    [
        "operation", "requestId", "httpMethod", "route", "statusCode", "durationMs", "outcome",
        "traceId", "spanId", "correlationId", "readinessReason", "errorCode", "stage", "pendingCount",
        "failedCount", "oldestPendingAgeSeconds", "publicationOutcomes", "consumptionOutcomes", "redeliveries",
        "lockDurationSamples", "averageLockDurationSeconds", "maximumLockDurationSeconds", "lockDurationOverTargetCount",
        "mediaType", "cohort", "sampleCount", "durationSampleCount", "durationSumSeconds", "durationMaximumSeconds",
        "withinTargetCount", "durationBuckets", "failureStage", "activeCount", "reconcilingCount"
    ];

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var fields = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["severity"] = logEntry.LogLevel.ToString(),
            ["eventId"] = logEntry.EventId.Id,
            ["eventName"] = logEntry.EventId.Name ?? logEntry.Category,
            ["message"] = logEntry.Formatter(logEntry.State, null),
            ["serviceName"] = "scrapbook-backend",
            ["serviceVersion"] = ServiceVersion,
            ["environment"] = environment.EnvironmentName,
            ["instanceId"] = Environment.MachineName,
            ["component"] = "api"
        };
        scopeProvider?.ForEachScope(AddStructuredFields, fields);
        AddStructuredFields(logEntry.State, fields);
        textWriter.WriteLine(JsonSerializer.Serialize(fields));
    }

    private static void AddStructuredFields(object? state, Dictionary<string, object?> fields)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
        {
            return;
        }

        foreach (var property in properties)
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Key);
            if (StructuredFields.Contains(name) && property.Value is not null)
            {
                fields[name] = property.Value;
            }
        }
    }
}
