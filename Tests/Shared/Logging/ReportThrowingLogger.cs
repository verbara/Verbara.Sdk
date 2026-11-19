using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Tests.Shared.Logging;

/// <summary>
/// A host's logger that fails on the failure report itself: <c>Log</c> throws on every line containing one of the
/// given fragments, and writes nothing else, so the connection's own lines (connect, login, loss) go through untouched.
/// What it throws is <see cref="LoggerFailure"/>, so a test can tell the logger's exception from the subscriber's.
/// </summary>
internal sealed class ReportThrowingLogger<T>(params string[] fragments) : ILogger<T>
{
    private int _thrown;

    /// <summary>How many times <c>Log</c> threw.</summary>
    public int Thrown => Volatile.Read(ref _thrown);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = formatter(state, exception);
        foreach (var fragment in fragments)
        {
            if (line.Contains(fragment, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _thrown);
                throw new LoggerFailure();
            }
        }
    }
}

/// <summary>What a failing host logger throws.</summary>
internal sealed class LoggerFailure() : InvalidOperationException("The host's logging provider fails.");
