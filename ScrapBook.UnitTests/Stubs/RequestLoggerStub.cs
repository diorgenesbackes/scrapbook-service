using Microsoft.Extensions.Logging;
using ScrapBook.Api.Middlewares;

namespace ScrapBook.UnitTests.Stubs;

internal sealed class RequestLoggerStub : ILogger<EventRequestMiddleware>
{
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(formatter(state, exception));
}
