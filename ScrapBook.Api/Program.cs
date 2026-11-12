using ScrapBook.Api.Endpoints;
using ScrapBook.Api.Modules.Events;
using ScrapBook.Domain.Common;
using ScrapBook.Api.Modules.Identity;
using ScrapBook.Infrastructure.Modules.Events;
using ScrapBook.Infrastructure.Modules.Identity;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
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
builder.Services.AddCors(options => options.AddPolicy("EventsLocal", policy => policy
    .WithOrigins(developmentIdentity.AllowedOrigins.Select(origin => origin.TrimEnd('/')).ToArray())
    .WithMethods("GET", "POST", "PUT", "OPTIONS")
    .WithHeaders("Content-Type", "X-Correlation-Id")));

var app = builder.Build();
app.UseCors("EventsLocal");
app.UseDevelopmentOriginBoundary();
app.MapHealthEndpoints();
app.Run();
