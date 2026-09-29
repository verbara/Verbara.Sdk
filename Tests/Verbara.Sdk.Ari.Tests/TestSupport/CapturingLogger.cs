using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// One log entry as the code under test wrote it: its level and event, the rendered message, the
/// exception object itself, and the structured values of a <c>[LoggerMessage]</c> template.
/// </summary>
internal sealed record CapturedLogEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State)
{
    /// <summary>The event's name, which is the <c>[LoggerMessage]</c> method's name.</summary>
    public string? EventName => EventId.Name;

    /// <summary>The value logged under <paramref name="key"/>, such as <c>ChannelId</c>, or null.</summary>
    public object? Value(string key) =>
        State.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.Ordinal)).Value;
}

/// <summary>
/// Records every entry written to it, and completes a wait when a matching entry arrives, so a test
/// ends its wait on the entry it asserts rather than on a delay.
/// </summary>
/// <remarks>
/// Test files that already declare their own private capturing logger keep using it; this one is
/// for classes that do not have one.
/// </remarks>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<CapturedLogEntry> _entries = [];
    private readonly List<(Func<CapturedLogEntry, bool> Match, TaskCompletionSource<CapturedLogEntry> Signal)> _waiters = [];

    /// <summary>A copy of every entry so far, in the order they were written.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// Completes with the first entry that matches, whether it was written before this call or after.
    /// The caller bounds the wait.
    /// </summary>
    public Task<CapturedLogEntry> WhenLogged(Func<CapturedLogEntry, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(match);
            if (existing is not null)
                return Task.FromResult(existing);

            var signal = new TaskCompletionSource<CapturedLogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, signal));
            return signal.Task;
        }
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
        ArgumentNullException.ThrowIfNull(formatter);
        IReadOnlyList<KeyValuePair<string, object?>> values =
            state is IReadOnlyList<KeyValuePair<string, object?>> pairs ? [.. pairs] : [];
        var entry = new CapturedLogEntry(logLevel, eventId, formatter(state, exception), exception, values);

        List<TaskCompletionSource<CapturedLogEntry>> matched = [];
        lock (_gate)
        {
            _entries.Add(entry);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Match(entry))
                    continue;

                matched.Add(_waiters[i].Signal);
                _waiters.RemoveAt(i);
            }
        }

        foreach (var signal in matched)
            signal.TrySetResult(entry);
    }
}
