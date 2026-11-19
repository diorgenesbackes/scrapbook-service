using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Console;
using ScrapBook.Api.AuthenticationHandlers;
using ScrapBook.Api.ExceptionHandlers;
using ScrapBook.Api.Formatters;
using ScrapBook.Api.HealthChecks;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Options;
using ScrapBook.Api.Writers;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Infrastructure.Persistence;
using ScrapBook.Infrastructure.Probes;

var builder = WebApplication.CreateBuilder(args);
DatabaseOptions database;
DevelopmentIdentityOptions identity;
try
{
    database = DatabaseOptions.FromConfiguration(builder.Configuration);
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
if (builder.Configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
{
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.Configure<ExceptionHandlerOptions>(options => options.SuppressDiagnosticsCallback = _ => true);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(identity);
builder.Services.AddDbContext<ScrapBookDbContext>(options => options.UseNpgsql(database.ConnectionString));
builder.Services.AddScoped<IPostgresReadinessProbe, EfPostgresReadinessProbe>();
builder.Services.AddAuthentication(DevelopmentIdentityAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevelopmentIdentityAuthenticationHandler>(
        DevelopmentIdentityAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks().AddCheck<PostgresReadinessHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();
app.UseRouting();
app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();
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
