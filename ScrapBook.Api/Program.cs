using ScrapBook.Api.Endpoints;
using ScrapBook.Api.Modules.Events;
using ScrapBook.Domain.Common;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddEventsDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BusinessClock>();

var app = builder.Build();
app.MapHealthEndpoints();
app.Run();
