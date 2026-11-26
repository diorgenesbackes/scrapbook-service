using System.Globalization;
using Amazon;
using Amazon.S3;
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
using ScrapBook.Api.Workers;
using ScrapBook.Api.Writers;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Codecs;
using ScrapBook.Infrastructure.Decoders;
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

var builder = WebApplication.CreateBuilder(args);
DatabaseOptions database;
DevelopmentIdentityOptions identity;
PublicUiOriginOptions ui;
InvitationProtectionOptions invitationProtection;
SubmissionProtectionOptions submissionProtection;
PhotoSubmissionsOptions photoSubmissions;
try
{
    database = DatabaseOptions.FromConfiguration(builder.Configuration);
    ui = PublicUiOriginOptions.FromConfiguration(builder.Configuration);
    invitationProtection = InvitationProtectionOptions.FromConfiguration(builder.Configuration);
    submissionProtection = SubmissionProtectionOptions.FromConfiguration(builder.Configuration);
    photoSubmissions = PhotoSubmissionsOptions.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment(),
        submissionProtection.KeyId is not null);
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
    builder.Services.AddSingleton(new PhotoSubmissionSettings(photoSubmissions.Enabled, photoSubmissions.Bucket));
    builder.Services.AddScoped<IPhotoSubmissionRepository, PhotoSubmissionRepository>();
    builder.Services.AddScoped<PhotoSubmissionService>();
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
