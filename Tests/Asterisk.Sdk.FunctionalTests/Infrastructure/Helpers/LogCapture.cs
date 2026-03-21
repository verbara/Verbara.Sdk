namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using Microsoft.Extensions.Logging;

public sealed class LogCapture : ILoggerProvider, IDisposable
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return [.. _entries]; }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose() { }

    internal void Add(LogEntry entry)
    {
        lock (_lock) _entries.Add(entry);
    }

    public bool ContainsErrors() =>
        Entries.Any(e => e.Level >= LogLevel.Error);

    public IEnumerable<LogEntry> GetErrors() =>
        Entries.Where(e => e.Level >= LogLevel.Error);

    private sealed class CaptureLogger(LogCapture capture, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            capture.Add(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}

public sealed record LogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception);
