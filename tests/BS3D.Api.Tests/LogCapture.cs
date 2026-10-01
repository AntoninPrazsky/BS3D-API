using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BS3D.Api.Tests;

/// <summary>
/// Every line the service logs, formatted as the journal would get it, so a test can say what never reaches the
/// journal (issue #2: the log is not a register of addresses).
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(LogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => capture._lines.Enqueue(formatter(state, exception));
    }
}
