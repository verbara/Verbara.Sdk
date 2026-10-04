using System.Diagnostics.Metrics;
using System.Globalization;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// Shared pieces of the dispatch tests that pin what a subscriber's failure, a token handler and a reconnect give-up
/// leave in the log and on the AMI instruments: a real <see cref="AmiConnection"/> over <see cref="PipedSocketFactory"/>,
/// a logger that keeps level, format, state and exception in order, and a listener that sums the AMI counters by name.
/// </summary>
/// <remarks>
/// Time enters only as <see cref="Bound"/>, a hang bound: every wait ends on the signal it asserts.
/// </remarks>
internal static class AmiDispatchTestKit
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>The peer of the event written last, so a subscriber that reaches it has seen every event before it.</summary>
    public const string Sentinel = "SIP/sentinel";

    public static string PeerName(int i) => "SIP/" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes <paramref name="count"/> PeerStatus events, peers <c>SIP/from</c> onwards.</summary>
    public static async Task WritePeersAsync(PipedSocket socket, int from, int count)
    {
        for (var i = from; i < from + count; i++)
            (await socket.WriteEventAsync("PeerStatus", [new("Peer", PeerName(i))])).Should().BeTrue();
    }

    public static async Task WriteSentinelAsync(PipedSocket socket) =>
        (await socket.WriteEventAsync("PeerStatus", [new("Peer", Sentinel)])).Should().BeTrue();

    public static bool IsSentinel(ManagerEvent evt) => evt is PeerStatusEvent { Peer: Sentinel };

    public static AmiConnection Create(PipedSocketFactory factory, ILogger<AmiConnection> logger,
        Action<AmiConnectionOptions>? configure = null)
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
        };
        configure?.Invoke(options);
        return new AmiConnection(Options.Create(options), factory, logger);
    }

    /// <summary>Connects, with the next socket's peer completing the login; returns that socket.</summary>
    public static async Task<PipedSocket> ConnectAsync(AmiConnection connection, PipedSocketFactory factory,
        CancellationTokenSource peer)
    {
        var loggedIn = Task.Run(async () =>
        {
            var socket = await factory.NextAsync(peer.Token);
            await socket.CompleteLoginAsync(peer.Token);
            return socket;
        }, peer.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await loggedIn.WaitAsync(Bound);
    }

    public static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public static async Task<T?> ResultWithinBoundAsync<T>(Task<T> task) where T : class
    {
        try
        {
            return await task.WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}

/// <summary>One log entry: its level, message format, rendered line, structured state and exception.</summary>
internal sealed record DispatchLogEntry(LogLevel Level, string? Format, string Line,
    IReadOnlyDictionary<string, object?> State, Exception? Exception);

/// <summary>
/// A logger that keeps every entry in the order it was written, signals a line once it is logged, and can run
/// <see cref="Act"/> inside the <c>ActOccurrence</c>-th line containing <c>ActOn</c>, on the thread that writes it and
/// before the write returns — so a test can place a call at a fixed point of the connection's own work.
/// </summary>
internal sealed class DispatchLogger : ILogger<AmiConnection>
{
    private readonly Lock _gate = new();
    private readonly List<DispatchLogEntry> _entries = [];
    private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];
    private int _seen;

    /// <summary>The fragment whose <see cref="ActOccurrence"/>-th line runs <see cref="Act"/>.</summary>
    public string? ActOn { get; init; }

    public int ActOccurrence { get; init; } = 1;

    public Action? Act { get; set; }

    public IReadOnlyList<DispatchLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Every entry whose rendered line contains <paramref name="fragment"/>, in order.</summary>
    public List<DispatchLogEntry> Containing(string fragment) =>
        [.. Entries.Where(e => e.Line.Contains(fragment, StringComparison.Ordinal))];

    public Task Logged(string fragment)
    {
        lock (_gate)
        {
            if (_entries.Exists(e => e.Line.Contains(fragment, StringComparison.Ordinal)))
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((fragment, signal));
            return signal.Task;
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
                values[pair.Key] = pair.Value;
        }

        var line = formatter(state, exception);
        var format = values.TryGetValue("{OriginalFormat}", out var f) ? f as string : null;
        List<TaskCompletionSource> fired = [];
        lock (_gate)
        {
            _entries.Add(new DispatchLogEntry(logLevel, format, line, values, exception));
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (line.Contains(_waiters[i].Fragment, StringComparison.Ordinal))
                {
                    fired.Add(_waiters[i].Signal);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var signal in fired)
            signal.TrySetResult();

        if (ActOn is not null && line.Contains(ActOn, StringComparison.Ordinal)
            && Interlocked.Increment(ref _seen) == ActOccurrence)
        {
            Act?.Invoke();
        }
    }
}

/// <summary>
/// Sums the measurements of the <c>Verbara.Sdk.Ami</c> counters it is given, by instrument name, process-wide, while it
/// is alive. Matched by name, never by reference to the meter's fields, so it can listen for an instrument that does
/// not exist yet and read 0 for it.
/// </summary>
internal sealed class AmiCounterTotals : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly HashSet<string> _names;
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _published = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public AmiCounterTotals(params string[] names)
    {
        _names = new HashSet<string>(names, StringComparer.Ordinal);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Verbara.Sdk.Ami" && _names.Contains(instrument.Name))
            {
                lock (_gate)
                    _published.Add(instrument.Name);
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (SkipOnce is { } skip && skip == instrument.Name)
            {
                SkipOnce = null;
                return;
            }

            lock (_gate)
                _totals[instrument.Name] = _totals.GetValueOrDefault(instrument.Name) + value;
        });
        _listener.Start();
    }

    /// <summary>Infrastructure liveness control only: the next measurement on this instrument is not summed.</summary>
    public string? SkipOnce { get; set; }

    public long Total(string name)
    {
        lock (_gate)
            return _totals.GetValueOrDefault(name);
    }

    public bool Published(string name)
    {
        lock (_gate)
            return _published.Contains(name);
    }

    public void Dispose() => _listener.Dispose();
}
