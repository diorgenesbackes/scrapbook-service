using ScrapBook.Api.DataProtectors;
using ScrapBook.Api.Extensions;
using ScrapBook.Api.InputFormatters;
using ScrapBook.Api.Middlewares;
using ScrapBook.Api.Options;
using ScrapBook.Application.Services;
using ScrapBook.Api.Transformers;
using ScrapBook.Api.Validators;
using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Clocks;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Policies;
using ScrapBook.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => { options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"; options.IncludeScopes = false; });
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
var samplingRate = builder.Configuration.GetValue<double?>("Events:TraceSamplingRate") ?? (builder.Environment.IsDevelopment() ? 1 : 0);
builder.Services.AddSingleton(_ => new EventTelemetryService(builder.Environment.EnvironmentName,
    typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown", samplingRate));
builder.Services.AddControllers(options =>
{
    options.InputFormatters.Clear();
    options.InputFormatters.Add(new EventMvcJsonInputFormatter());
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
}).ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = _ => throw EventRequestValidator.Invalid("body", "Revise os campos obrigatórios, tipos e parâmetros da requisição.");
});
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer<EventOpenApiDocumentTransformer>();
    options.AddDocumentTransformer<PublicEventOpenApiDocumentTransformer>();
});
builder.Services.AddCursorDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BusinessClock>();
var developmentIdentity = builder.Configuration.GetSection("DevelopmentIdentity").Get<DevelopmentIdentityOptions>()
    ?? new DevelopmentIdentityOptions();
developmentIdentity.Validate(builder.Environment, builder.Configuration);
builder.Services.AddSingleton(developmentIdentity);
builder.Services.AddPersistence(builder.Configuration.GetConnectionString("ScrapBook"));
builder.Services.AddDevelopmentIdentity(developmentIdentity.Enabled, developmentIdentity.AccountId);
builder.Services.AddSingleton<IEventCursorProtector, EventCursorDataProtector>();
builder.Services.AddSingleton<IEventCursorService, EventCursorService>();
builder.Services.AddScoped<IEventService, EventService>();
var publicSite = builder.Configuration.GetSection("PublicSite").Get<PublicSiteOptions>() ?? new PublicSiteOptions();
builder.Services.AddSingleton(new PublicSiteModel(PublicEventLinkPolicy.NormalizeOrigin(publicSite.Origin, publicSite.AllowHttpLoopback)));
builder.Services.AddScoped<IEventSharingService, EventSharingService>();
builder.Services.AddScoped<IPublicEventService, PublicEventService>();
builder.Services.AddCors(options => options.AddPolicy("EventsLocal", policy => policy
    .WithOrigins(developmentIdentity.AllowedOrigins.Select(origin => origin.TrimEnd('/')).ToArray())
    .WithMethods("GET", "POST", "PUT", "OPTIONS")
    .WithHeaders("Content-Type", "X-Correlation-Id")
    .WithExposedHeaders("X-Correlation-Id")));

var app = builder.Build();
app.UseRouting();
app.UseCors("EventsLocal");
app.UseMiddleware<EventRequestMiddleware>();
app.MapControllers();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.Run();
