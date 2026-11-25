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
using ScrapBook.Api.Writers;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Infrastructure.Codecs;
using ScrapBook.Infrastructure.Persistence;
using ScrapBook.Infrastructure.Probes;
using ScrapBook.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);
DatabaseOptions database;
DevelopmentIdentityOptions identity;
PublicUiOriginOptions ui;
InvitationProtectionOptions invitationProtection;
SubmissionProtectionOptions submissionProtection;
try
{
    database = DatabaseOptions.FromConfiguration(builder.Configuration);
    ui = PublicUiOriginOptions.FromConfiguration(builder.Configuration);
    invitationProtection = InvitationProtectionOptions.FromConfiguration(builder.Configuration);
    submissionProtection = SubmissionProtectionOptions.FromConfiguration(builder.Configuration);
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
