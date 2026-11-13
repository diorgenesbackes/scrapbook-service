using ScrapBook.Api.Endpoints;
using ScrapBook.Api.Modules.Events;
using ScrapBook.Domain.Common;
using ScrapBook.Api.Modules.Identity;
using ScrapBook.Infrastructure.Modules.Events;
using ScrapBook.Infrastructure.Modules.Identity;
using ScrapBook.Application.Modules.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => { options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"; options.IncludeScopes = true; });
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
var samplingRate = builder.Configuration.GetValue<double?>("Events:TraceSamplingRate") ?? (builder.Environment.IsDevelopment() ? 1 : 0);
builder.Services.AddSingleton(_ => new EventsTelemetry(builder.Environment.EnvironmentName,
    typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown", samplingRate));
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer<EventsOpenApi>());
builder.Services.AddEventsDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BusinessClock>();
var developmentIdentity = builder.Configuration.GetSection("DevelopmentIdentity").Get<DevelopmentIdentityOptions>()
    ?? new DevelopmentIdentityOptions();
developmentIdentity.Validate(builder.Environment, builder.Configuration);
builder.Services.AddSingleton(developmentIdentity);
var connectionString = builder.Configuration.GetConnectionString("ScrapBook");
builder.Services.AddEventsPersistence(connectionString);
builder.Services.AddDevelopmentIdentity(connectionString, developmentIdentity.Enabled, developmentIdentity.AccountId);
builder.Services.AddSingleton<IEventCursorCodec, EventCursorCodec>();
builder.Services.AddScoped<EventService>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddCors(options => options.AddPolicy("EventsLocal", policy => policy
    .WithOrigins(developmentIdentity.AllowedOrigins.Select(origin => origin.TrimEnd('/')).ToArray())
    .WithMethods("GET", "POST", "PUT", "OPTIONS")
    .WithHeaders("Content-Type", "X-Correlation-Id")));

var app = builder.Build();
app.UseCors("EventsLocal");
app.UseMiddleware<EventsBoundary>();
app.MapHealthEndpoints();
app.MapEventEndpoints();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.Run();
