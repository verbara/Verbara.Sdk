using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps what it was told, so a test can assert that a
/// terminal log entry was written rather than inferring it from a metric.
/// </summary>
/// <remarks><see cref="Entries"/> hands out a snapshot taken under the write lock, never the backing
/// list (ADR-0045 rule 3). <c>EventId.Name</c> is the <c>[LoggerMessage]</c> method name, which is
/// what these assertions match on.</remarks>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var entry = new LogEntry(logLevel, eventId, formatter(state, exception), exception);
        lock (_gate) _entries.Add(entry);
    }

    internal sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
}
