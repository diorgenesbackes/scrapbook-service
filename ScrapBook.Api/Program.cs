using ScrapBook.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthEndpoints();
app.Run();
