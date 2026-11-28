using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ScrapBook.Api.Options;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Persistence;
using ScrapBook.Infrastructure.Repositories;

namespace ScrapBook.Api.Runners;

public static class MediaPreparationRecoveryRunner
{
    public const string Argument = "media-recover";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (arguments is not [var mode, .. var parameters]) return WriteCode("MEDIA_RECOVERY_INVALID_ARGUMENT", 2);

        var apply = parameters is [.., "--apply"];
        if (apply) parameters = parameters[..^1];
        var command = ReadCommand(mode, parameters, apply);
        if (command is null) return WriteCode("MEDIA_RECOVERY_INVALID_ARGUMENT", 2);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            if (!bool.TryParse(builder.Configuration["MediaRecovery:Enabled"] ?? "false", out var enabled))
            {
                return WriteCode("MEDIA_RECOVERY_INVALID_CONFIGURATION", 4);
            }
            if (!enabled) return WriteCode("MEDIA_RECOVERY_DISABLED", 3);

            var database = DatabaseOptions.ForMediaRecovery(builder.Configuration);
            builder.Services.AddDbContext<ScrapBookDbContext>(options => options.UseNpgsql(database.ConnectionString));
            Register(builder.Services);
            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            return await ExecuteAsync(command, scope.ServiceProvider, deadline.Token);
        }
        catch (Exception error) when (error is StartupConfigurationException or PostgresUnavailableException
            or PostgresWriteRejectedException or MediaPreparationWriteException or DbException or IOException)
        {
            return WriteCode("MEDIA_RECOVERY_UNAVAILABLE", 4);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return WriteCode("MEDIA_RECOVERY_UNAVAILABLE", 4);
        }
    }

    private static void Register(IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<MediaPreparationRepository>();
        services.AddScoped<IMediaAdmissionRepository>(provider => provider.GetRequiredService<MediaPreparationRepository>());
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IMediaRecoveryAccessRepository, MediaRecoveryAccessRepository>();
        services.AddScoped<IMediaPreparationRecoveryRepository, MediaPreparationRecoveryRepository>();
        services.AddScoped<IMediaHistoryRepository, MediaHistoryRepository>();
        services.AddScoped<MediaRecoveryAccessService>();
        services.AddScoped<MediaPreparationRecoveryService>();
        services.AddScoped<MediaHistoryService>();
    }

    private static object? ReadCommand(string mode, string[] parameters, bool apply) => mode switch
    {
        "history" => ReadBatchCommand(parameters, apply),
        "redrive" => ReadRedriveCommand(parameters, apply),
        "reopen" => ReadReopenCommand(parameters, apply),
        _ => null
    };

    private static async Task<int> ExecuteAsync(object command, IServiceProvider services, CancellationToken cancellationToken) =>
        command switch
        {
            MediaRecoveryBatchCommand batch => WriteBatch(await services.GetRequiredService<MediaHistoryService>()
                .ScanAsync(batch, cancellationToken)),
            MediaRedriveCommand redrive => WriteResult(await services.GetRequiredService<MediaPreparationRecoveryService>()
                .RedriveAsync(redrive, cancellationToken)),
            MediaReopenCommand reopen => WriteResult(await services.GetRequiredService<MediaPreparationRecoveryService>()
                .ReopenAsync(reopen, cancellationToken)),
            _ => WriteCode("MEDIA_RECOVERY_INVALID_ARGUMENT", 2)
        };

    private static MediaRecoveryBatchCommand? ReadBatchCommand(string[] parameters, bool apply)
    {
        if (parameters is not [var eventText, var afterText, var limitText, var incident, var revision]
            || !ReadId(eventText, out var eventId) || !ReadId(afterText, out var afterId) || !ReadInt(limitText, out var limit))
        {
            return null;
        }

        var command = new MediaRecoveryBatchCommand(eventId, afterId, limit, incident, revision, apply);
        return MediaRecoveryAccessService.IsValid(command) ? command : null;
    }

    private static MediaRedriveCommand? ReadRedriveCommand(string[] parameters, bool apply)
    {
        if (parameters is not [var eventText, var messageText, var incident, var revision]
            || !ReadId(eventText, out var eventId) || !Guid.TryParseExact(messageText, "D", out var messageId))
        {
            return null;
        }

        var command = new MediaRedriveCommand(eventId, messageId, incident, revision, apply);
        return MediaPreparationRecoveryService.IsValid(command) ? command : null;
    }

    private static MediaReopenCommand? ReadReopenCommand(string[] parameters, bool apply)
    {
        if (parameters is not [var eventText, var fileText, var profileText, var cycleText, var incident, var revision]
            || !ReadId(eventText, out var eventId) || !ReadId(fileText, out var fileId)
            || !ReadInt(profileText, out var profileVersion) || !ReadInt(cycleText, out var expectedCycle))
        {
            return null;
        }

        var command = new MediaReopenCommand(eventId, fileId, profileVersion, expectedCycle, incident, revision, apply);
        return MediaPreparationRecoveryService.IsValid(command) ? command : null;
    }

    private static bool ReadId(string value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static bool ReadInt(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static int WriteResult(MediaRecoveryResult result) =>
        WriteCode(result.Code, result.Succeeded ? 0 : 3);

    private static int WriteBatch(MediaRecoveryBatchResult result)
    {
        var code = result.Code == "OK" ? 0 : 3;
        (code == 0 ? Console.Out : Console.Error).WriteLine(JsonSerializer.Serialize(new
        {
            result.Code,
            NextId = result.NextId.ToString(CultureInfo.InvariantCulture),
            result.Examined,
            result.Changed,
            result.Rejected,
            result.Deferred
        }, Json));
        return code;
    }

    private static int WriteCode(string code, int exit)
    {
        (exit == 0 ? Console.Out : Console.Error).WriteLine(JsonSerializer.Serialize(new { code }, Json));
        return exit;
    }
}
