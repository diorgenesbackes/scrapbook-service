using System.Globalization;
using Amazon;
using Amazon.MediaConvert;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Console;
using ScrapBook.Api.AuthenticationHandlers;
using ScrapBook.Api.Codecs;
using ScrapBook.Api.ExceptionHandlers;
using ScrapBook.Api.Formatters;
using ScrapBook.Api.HealthChecks;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Options;
using ScrapBook.Api.Runners;
using ScrapBook.Api.Workers;
using ScrapBook.Api.Writers;
using ScrapBook.Application.Diagnostics;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Codecs;
using ScrapBook.Infrastructure.Decoders;
using ScrapBook.Infrastructure.Messaging;
using ScrapBook.Infrastructure.Persistence;
using ScrapBook.Infrastructure.Probes;
using ScrapBook.Infrastructure.Repositories;
using ScrapBook.Infrastructure.Storage;

if (args is [ProcessPhotoSubmissionDecoder.Argument, var length])
{
    return int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
        ? await PhotoDecoderRunner.RunAsync(bytes, CancellationToken.None)
        : ProcessPhotoSubmissionDecoder.InvalidContentExitCode;
}

if (args is [ProcessPhotoVariantProcessor.Argument, var photoLength, var imageSide, var thumbnailSide, var quality])
{
    return int.TryParse(photoLength, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
        && int.TryParse(imageSide, NumberStyles.None, CultureInfo.InvariantCulture, out var imageMaximum)
        && int.TryParse(thumbnailSide, NumberStyles.None, CultureInfo.InvariantCulture, out var thumbnailMaximum)
        && int.TryParse(quality, NumberStyles.None, CultureInfo.InvariantCulture, out var jpegQuality)
        ? await PhotoVariantRunner.RunAsync(bytes, new(imageMaximum, thumbnailMaximum, jpegQuality), CancellationToken.None)
        : ProcessPhotoSubmissionDecoder.InvalidContentExitCode;
}

if (args is [EventStructuralLockRecoveryRunner.Argument, .. var recoveryArguments])
{
    return await RunOperationalModeAsync(token => EventStructuralLockRecoveryRunner.RunAsync(recoveryArguments, token));
}

if (args is [MediaPreparationRecoveryRunner.Argument, .. var mediaRecoveryArguments])
{
    return await RunOperationalModeAsync(token => MediaPreparationRecoveryRunner.RunAsync(mediaRecoveryArguments, token));
}

var builder = WebApplication.CreateBuilder(args);
DatabaseOptions database;
DevelopmentIdentityOptions identity;
PublicUiOriginOptions ui;
InvitationProtectionOptions invitationProtection;
SubmissionProtectionOptions submissionProtection;
PhotoSubmissionsOptions photoSubmissions;
EventStructuralLockOptions eventStructuralLock;
MediaProcessingOptions mediaProcessing;
MediaAudioProcessingOptions mediaAudioProcessing;
try
{
    database = DatabaseOptions.FromConfiguration(builder.Configuration);
    ui = PublicUiOriginOptions.FromConfiguration(builder.Configuration);
    invitationProtection = InvitationProtectionOptions.FromConfiguration(builder.Configuration);
    submissionProtection = SubmissionProtectionOptions.FromConfiguration(builder.Configuration);
    photoSubmissions = PhotoSubmissionsOptions.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment(),
        submissionProtection.KeyId is not null);
    eventStructuralLock = EventStructuralLockOptions.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment());
    mediaProcessing = MediaProcessingOptions.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment(),
        photoSubmissions, eventStructuralLock);
    mediaAudioProcessing = MediaAudioProcessingOptions.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment(),
        photoSubmissions, mediaProcessing, eventStructuralLock);
    var isContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
        "true", StringComparison.OrdinalIgnoreCase);
    identity = DevelopmentIdentityOptions.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment(), isContainer);
}
catch (StartupConfigurationException error)
{
    Console.Error.WriteLine($"CONFIGURATION_INVALID {error.OptionName}");
    return 1;
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole(settings => settings.FormatterName = ScrapBookJsonConsoleFormatter.FormatterName)
    .AddConsoleFormatter<ScrapBookJsonConsoleFormatter, ConsoleFormatterOptions>();
if (identity.Enabled)
{
    builder.WebHost.ConfigureKestrel(options => options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false));
}
builder.Services.AddControllers();
if (ui.Origin is { } origin)
{
    builder.Services.AddCors(options => options.AddPolicy(
        PublicUiOriginOptions.PolicyName,
        policy => policy.WithOrigins(origin)
            .WithMethods(HttpMethods.Get, HttpMethods.Head, HttpMethods.Post, HttpMethods.Put)
            .WithHeaders(CorrelationMiddleware.HeaderName, "Content-Type", "Idempotency-Key", "Authorization")
            .WithExposedHeaders(CorrelationMiddleware.HeaderName, "Retry-After", "Location")));
}

var protection = builder.Services.AddDataProtection();
if (builder.Configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
{
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.Configure<ExceptionHandlerOptions>(options => options.SuppressDiagnosticsCallback = _ => true);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(ui);
builder.Services.AddDbContext<ScrapBookDbContext>(options => options.UseNpgsql(database.ConnectionString));
builder.Services.AddScoped<IPostgresReadinessProbe, EfPostgresReadinessProbe>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEventCursorCodec, EventCursorCodec>();
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IEventWriteRepository, EventWriteRepository>();
builder.Services.AddScoped<IInvitationRepository, InvitationRepository>();
builder.Services.AddScoped<ISubmissionRepository, SubmissionRepository>();
builder.Services.AddSingleton<ISubmissionProtectionCodec>(new SubmissionProtectionCodec(
    submissionProtection.KeyId, submissionProtection.Keys));
builder.Services.AddScoped<SubmissionService>();
if (photoSubmissions.Enabled)
{
    builder.Services.AddSingleton(photoSubmissions);
    builder.Services.AddSingleton(new PhotoSubmissionSettings(photoSubmissions.Enabled, photoSubmissions.Bucket,
        photoSubmissions.VideoEnabled, photoSubmissions.AudioEnabled));
    builder.Services.AddScoped<PhotoSubmissionRepository>();
    builder.Services.AddScoped<IPhotoSubmissionRepository>(services => services.GetRequiredService<PhotoSubmissionRepository>());
    builder.Services.AddScoped<IVideoSubmissionRepository>(services => services.GetRequiredService<PhotoSubmissionRepository>());
    builder.Services.AddScoped<PhotoSubmissionService>();
    builder.Services.AddScoped<VideoSubmissionService>();
    builder.Services.AddScoped<AudioSubmissionService>();
    builder.Services.AddSingleton<IPhotoSubmissionReporter, PhotoSubmissionReporter>();
    builder.Services.AddSingleton<IPhotoSubmissionDecoder, ProcessPhotoSubmissionDecoder>();
    builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(StorageConfiguration(photoSubmissions.StorageServiceUrl)));
    builder.Services.AddSingleton<IPhotoSubmissionStorage>(services =>
    {
        var storage = services.GetRequiredService<IAmazonS3>();
        var browserEndpoint = photoSubmissions.BrowserStorageServiceUrl;
        var browserClient = browserEndpoint is null ? storage : new AmazonS3Client(StorageConfiguration(browserEndpoint));
        var effectiveEndpoint = browserEndpoint ?? photoSubmissions.StorageServiceUrl;
        var protocol = effectiveEndpoint is not null && new Uri(effectiveEndpoint).Scheme == Uri.UriSchemeHttp
            ? Protocol.HTTP : Protocol.HTTPS;
        return new S3PhotoSubmissionStorage(storage, browserClient, protocol);
    });
    builder.Services.AddSingleton<IVideoSubmissionStorage>(services =>
        (IVideoSubmissionStorage)services.GetRequiredService<IPhotoSubmissionStorage>());
    builder.Services.AddSingleton<IStorageObjectReader>(services =>
        services.GetRequiredService<IVideoSubmissionStorage>());
    builder.Services.AddSingleton<IAudioSubmissionDecoder>(services =>
        new S3AudioSubmissionDecoder(services.GetRequiredService<IAmazonS3>(), new FfprobeAudioDecoder(photoSubmissions.VideoProbePath)));
    builder.Services.AddSingleton<IVideoSubmissionDecoder>(services =>
        new S3VideoSubmissionDecoder(services.GetRequiredService<IAmazonS3>(), new FfprobeVideoDecoder(photoSubmissions.VideoProbePath)));
    builder.Services.AddSingleton<IAmazonSQS>(_ =>
    {
        var config = new AmazonSQSConfig();
        if (photoSubmissions.QueueServiceUrl is { } endpoint)
        {
            config.ServiceURL = endpoint;
            config.AuthenticationRegion = photoSubmissions.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(photoSubmissions.Region);
        }
        return new AmazonSQSClient(config);
    });
    builder.Services.AddHostedService<PhotoReceiptWorker>();
    builder.Services.AddHostedService<PhotoSubmissionWorker>();
}
if (eventStructuralLock.Enabled || mediaProcessing.Enabled)
{
    builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();
}
if (eventStructuralLock.Enabled)
{
    builder.Services.AddScoped<IEventStructuralLockRepository, EventStructuralLockRepository>();
    builder.Services.AddScoped<OutboxPublisherService>();
    builder.Services.AddScoped<EventStructuralLockService>();
    builder.Services.AddScoped<EventStructuralLockMonitoringService>();
    builder.Services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
        new AmazonSimpleNotificationServiceClient(MessagingConfiguration(new AmazonSimpleNotificationServiceConfig(),
            eventStructuralLock.Region, eventStructuralLock.SnsServiceUrl)));
    builder.Services.AddKeyedSingleton<IAmazonSQS>(EventStructuralLockOptions.SectionName, (_, _) =>
        new AmazonSQSClient(MessagingConfiguration(new AmazonSQSConfig(), eventStructuralLock.Region, eventStructuralLock.SqsServiceUrl)));
    builder.Services.AddSingleton<IOutboxPublisher>(services => new SnsOutboxPublisher(
        services.GetRequiredService<IAmazonSimpleNotificationService>(), eventStructuralLock.TopicArn));
    builder.Services.AddSingleton<IEventStructuralLockQueue>(services => new SqsEventStructuralLockConsumer(
        services.GetRequiredKeyedService<IAmazonSQS>(EventStructuralLockOptions.SectionName), eventStructuralLock.QueueUrl));
    builder.Services.AddHostedService<OutboxPublisherWorker>();
    builder.Services.AddHostedService<OutboxMonitoringWorker>();
    builder.Services.AddHostedService<EventStructuralLockWorker>();
}
if (mediaProcessing.Enabled)
{
    builder.Services.AddSingleton(mediaProcessing);
    builder.Services.AddSingleton<MediaProcessingTelemetry>();
    builder.Services.AddScoped<MediaProcessingMonitoringService>();
    builder.Services.AddScoped<IMediaProcessingMonitoringRepository, MediaProcessingMonitoringRepository>();
    builder.Services.AddScoped<MediaPreparationRepository>();
    builder.Services.AddScoped<IMediaAdmissionRepository>(services => services.GetRequiredService<MediaPreparationRepository>());
    builder.Services.AddScoped<IMediaPreparationRepository>(services => services.GetRequiredService<MediaPreparationRepository>());
    builder.Services.AddScoped(services => new MediaAdmissionService(services.GetRequiredService<IMediaAdmissionRepository>(),
        services.GetRequiredService<IOutboxRepository>(), services.GetRequiredService<TimeProvider>(), mediaProcessing.HistoricalBefore,
        services.GetRequiredService<MediaProcessingTelemetry>()));
    builder.Services.AddScoped<MediaPreparationService>();
    builder.Services.AddScoped<MediaOutboxPublisherService>(services => new MediaOutboxPublisherService(
        services.GetRequiredService<IOutboxRepository>(),
        services.GetRequiredKeyedService<IOutboxPublisher>(MediaProcessingOptions.SectionName),
        services.GetRequiredService<IMediaAdmissionRepository>(),
        services.GetRequiredService<IMediaPreparationRepository>(),
        services.GetRequiredService<TimeProvider>(), mediaProcessing.HistoricalBefore,
        services.GetRequiredService<MediaProcessingTelemetry>()));
    builder.Services.AddKeyedSingleton<IAmazonSimpleNotificationService>(MediaProcessingOptions.SectionName, (_, _) =>
        new AmazonSimpleNotificationServiceClient(MessagingConfiguration(new AmazonSimpleNotificationServiceConfig(),
            mediaProcessing.Region, mediaProcessing.SnsServiceUrl)));
    builder.Services.AddKeyedSingleton<IAmazonSQS>(MediaProcessingOptions.SectionName, (_, _) =>
        new AmazonSQSClient(MessagingConfiguration(new AmazonSQSConfig(), mediaProcessing.Region, mediaProcessing.SqsServiceUrl)));
    builder.Services.AddKeyedSingleton<IOutboxPublisher>(MediaProcessingOptions.SectionName, (services, _) => new SnsOutboxPublisher(
        services.GetRequiredKeyedService<IAmazonSimpleNotificationService>(MediaProcessingOptions.SectionName), mediaProcessing.TopicArn));
    builder.Services.AddSingleton<IMediaConfirmationQueue>(services => new SqsMediaConfirmationConsumer(
        services.GetRequiredKeyedService<IAmazonSQS>(MediaProcessingOptions.SectionName), mediaProcessing.QueueUrl));
    builder.Services.AddSingleton<IMediaVariantStorage, S3MediaVariantStorage>();
    builder.Services.AddSingleton<IPhotoVariantProcessor, ProcessPhotoVariantProcessor>();
    if (!mediaProcessing.Paused)
    {
        builder.Services.AddHostedService<MediaOutboxPublisherWorker>();
        builder.Services.AddHostedService<MediaAdmissionWorker>();
        builder.Services.AddHostedService<MediaPreparationWorker>();
    }
    builder.Services.AddHostedService<MediaProcessingMonitoringWorker>();
}
if (mediaAudioProcessing.Enabled)
{
    var settings = mediaAudioProcessing.Settings;
    builder.Services.AddSingleton(settings);
    builder.Services.AddScoped<IMediaAudioRepository, MediaAudioRepository>();
    builder.Services.AddScoped<MediaAudioPreparationService>();
    builder.Services.AddSingleton(_ => new AudioTranscodingDecoder(photoSubmissions.VideoProbePath));
    builder.Services.AddSingleton<IMediaAudioStorage, S3MediaAudioStorage>();
    if (settings.VideoEnabled)
    {
        builder.Services.AddSingleton(_ => new VideoTranscodingDecoder(photoSubmissions.VideoProbePath));
        builder.Services.AddSingleton<IMediaVideoStorage, S3MediaVideoStorage>();
    }
    builder.Services.AddSingleton<IAmazonMediaConvert>(_ => new AmazonMediaConvertClient(
        MessagingConfiguration(new AmazonMediaConvertConfig(), settings.Region, mediaAudioProcessing.MediaConvertServiceUrl)));
    builder.Services.AddSingleton<IMediaTranscodingProvider, MediaConvertProvider>();
    builder.Services.AddKeyedSingleton<IAmazonSQS>(MediaAudioProcessingOptions.SectionName, (_, _) =>
        new AmazonSQSClient(MessagingConfiguration(new AmazonSQSConfig(), settings.Region, mediaAudioProcessing.SqsServiceUrl)));
    builder.Services.AddSingleton<IMediaAudioResultQueue>(services => new SqsMediaAudioResultConsumer(
        services.GetRequiredKeyedService<IAmazonSQS>(MediaAudioProcessingOptions.SectionName), mediaAudioProcessing.ResultQueueUrl));
    builder.Services.AddHostedService<MediaAudioPreparationWorker>();
    builder.Services.AddHostedService<MediaAudioResultWorker>();
}
builder.Services.AddSingleton<IInvitationTokenCodec>(new InvitationTokenCodec(
    invitationProtection.HashKeyId, invitationProtection.HashKeys, invitationProtection.EncryptionKeyId,
    invitationProtection.EncryptionKeys));
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<InvitationAcceptanceService>();
builder.Services.AddScoped<EventOpeningService>();
builder.Services.AddScoped<EventDetailsService>();
builder.Services.AddScoped<PublicEventService>();
builder.Services.AddScoped<EventService>();
builder.Services.AddAuthentication(DevelopmentIdentityAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevelopmentIdentityAuthenticationHandler>(
        DevelopmentIdentityAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks().AddCheck<PostgresReadinessHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();
app.UseRouting();
app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();
if (ui.Origin is not null)
{
    app.UseCors(PublicUiOriginOptions.PolicyName);
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = (context, report) => HealthHttpResponseWriter.WriteAsync(context, report, context.RequestAborted)
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = (context, report) => HealthHttpResponseWriter.WriteAsync(context, report, context.RequestAborted)
});

await app.RunAsync();
return 0;

static async Task<int> RunOperationalModeAsync(Func<CancellationToken, Task<int>> run)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        return await run(cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        return 130;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}

AmazonS3Config StorageConfiguration(string? serviceUrl)
{
    var config = new AmazonS3Config { ForcePathStyle = photoSubmissions.ForcePathStyle };
    if (serviceUrl is not null)
    {
        config.ServiceURL = serviceUrl;
        config.AuthenticationRegion = photoSubmissions.Region;
    }
    else
    {
        config.RegionEndpoint = RegionEndpoint.GetBySystemName(photoSubmissions.Region);
    }
    return config;
}

static T MessagingConfiguration<T>(T config, string region, string? serviceUrl) where T : ClientConfig
{
    config.IgnoreConfiguredEndpointUrls = true;
    config.MaxErrorRetry = 0;
    if (serviceUrl is not null)
    {
        config.ServiceURL = serviceUrl;
        config.AuthenticationRegion = region;
    }
    else
    {
        config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
    }
    return config;
}
