using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Amazon;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using ScrapBook.Api.Options;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Persistence;
using ScrapBook.Infrastructure.Repositories;
using ScrapBook.Infrastructure.Storage;

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
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            });
            builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            builder.Configuration.AddEnvironmentVariables();
            builder.Logging.ClearProviders();
            if (!bool.TryParse(builder.Configuration["MediaRecovery:Enabled"] ?? "false", out var enabled))
            {
                return WriteCode("MEDIA_RECOVERY_INVALID_CONFIGURATION", 4);
            }
            if (!enabled) return WriteCode("MEDIA_RECOVERY_DISABLED", 3);

            var historyBefore = MediaHistoryOptions.ReadCutoff(builder.Configuration);
            if (mode == "history" && command is MediaRecoveryBatchCommand historical)
            {
                command = historical with
                {
                    HistoricalBefore = historyBefore
                    ?? throw new StartupConfigurationException("MediaRecovery:HistoryBeforeUtc")
                };
            }
            var database = DatabaseOptions.ForMediaRecovery(builder.Configuration);
            builder.Services.AddDbContext<ScrapBookDbContext>(options => options.UseNpgsql(database.ConnectionString));
            Register(builder.Services, historyBefore);
            if (mode == "cleanup" && command is MediaRecoveryBatchCommand cleanup)
            {
                command = cleanup with { CleanupObjectId = ReadCleanupObjectId(builder.Configuration, cleanup.Apply) };
                RegisterCleanup(builder);
            }
            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            return await ExecuteAsync(mode, command, scope.ServiceProvider, deadline.Token);
        }
        catch (Exception error) when (error is StartupConfigurationException or PostgresUnavailableException
            or PostgresWriteRejectedException or MediaPreparationWriteException or DbException or IOException
            or Amazon.Runtime.AmazonClientException)
        {
            return WriteCode("MEDIA_RECOVERY_UNAVAILABLE", 4);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return WriteCode("MEDIA_RECOVERY_UNAVAILABLE", 4);
        }
    }

    private static void Register(IServiceCollection services, DateTimeOffset? historyBefore)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<MediaPreparationRepository>();
        services.AddScoped<IMediaAdmissionRepository>(provider => provider.GetRequiredService<MediaPreparationRepository>());
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IMediaRecoveryAccessRepository, MediaRecoveryAccessRepository>();
        services.AddScoped<IMediaPreparationRecoveryRepository, MediaPreparationRecoveryRepository>();
        services.AddScoped<IMediaHistoryRepository, MediaHistoryRepository>();
        services.AddScoped<MediaRecoveryAccessService>();
        services.AddScoped(provider => new MediaPreparationRecoveryService(provider.GetRequiredService<IMediaPreparationRecoveryRepository>(),
            provider.GetRequiredService<IMediaAdmissionRepository>(), provider.GetRequiredService<IMediaRecoveryAccessRepository>(),
            provider.GetRequiredService<MediaRecoveryAccessService>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<MediaHistoryService>(), historyBefore));
        services.AddScoped<MediaHistoryService>();
    }

    private static void RegisterCleanup(HostApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection("PhotoSubmissions");
        var bucket = PhotoSubmissionsOptions.RequireBucket(settings["Bucket"]);
        var region = PhotoSubmissionsOptions.RequireRegion(settings["Region"]);
        var development = builder.Environment.IsDevelopment();
        var endpoint = PhotoSubmissionsOptions.ReadEndpoint(settings["StorageServiceUrl"], development);
        var config = new AmazonS3Config { IgnoreConfiguredEndpointUrls = true };
        if (endpoint is null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
        else
        {
            config.ForcePathStyle = true;
            config.ServiceURL = endpoint;
            config.AuthenticationRegion = region;
        }

        builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(config));
        builder.Services.AddSingleton<IMediaCleanupStorage, S3MediaCleanupStorage>();
        builder.Services.AddScoped<IMediaCleanupRepository, MediaCleanupRepository>();
        builder.Services.AddScoped(services => new MediaCleanupService(
            services.GetRequiredService<MediaRecoveryAccessService>(), services.GetRequiredService<IMediaRecoveryAccessRepository>(),
            services.GetRequiredService<IMediaCleanupRepository>(), services.GetRequiredService<IMediaCleanupStorage>(),
            services.GetRequiredService<TimeProvider>(), bucket));
    }

    private static long? ReadCleanupObjectId(IConfiguration configuration, bool apply)
    {
        const string key = "MediaRecovery:CleanupObjectId";
        var objectId = configuration[key];
        if (objectId is null && (!apply || configuration["PhotoSubmissions:StorageServiceUrl"] is not null)) return null;
        if (objectId is null || !ReadId(objectId, out var id) || id <= 0) throw new StartupConfigurationException(key);
        return id;
    }

    private static object? ReadCommand(string mode, string[] parameters, bool apply) => mode switch
    {
        "history" or "cleanup" => ReadBatchCommand(parameters, apply),
        "redrive" => ReadRedriveCommand(parameters, apply),
        "reopen" => ReadReopenCommand(parameters, apply),
        _ => null
    };

    private static async Task<int> ExecuteAsync(string mode, object command, IServiceProvider services,
        CancellationToken cancellationToken) =>
        command switch
        {
            MediaRecoveryBatchCommand batch when mode == "cleanup" => WriteBatch(await services.GetRequiredService<MediaCleanupService>()
                .ScanAsync(batch, cancellationToken)),
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
