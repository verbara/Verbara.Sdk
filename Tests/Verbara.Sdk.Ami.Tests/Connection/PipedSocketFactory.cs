using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Verbara.Sdk.Ami.Transport;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// Hands the connection a fresh <see cref="PipedSocket"/> on every <see cref="Create"/>, so a test can
/// play the Asterisk peer of each connect — including the ones the reconnect loop makes on its own.
/// </summary>
internal sealed class PipedSocketFactory : ISocketConnectionFactory
{
    private readonly Channel<PipedSocket> _created = Channel.CreateUnbounded<PipedSocket>();
    private readonly Lock _gate = new();
    private readonly List<PipedSocket> _all = [];
    private TaskCompletionSource<PipedSocket> _nextCreated = NewCreatedSignal();
    private bool _refuseConnects;

    /// <summary>
    /// How many sockets accept their connect. Every socket created after them refuses it, as a peer that
    /// is down does, so a test can make every reconnect fail. Unlimited by default.
    /// </summary>
    public int ConnectsAccepted { get; init; } = int.MaxValue;

    /// <summary>
    /// While <see langword="true"/>, every socket created refuses its connect, whatever <see cref="ConnectsAccepted"/>
    /// says. Unlike <see cref="ConnectsAccepted"/> it can be switched back, so a test can refuse the reconnect loop's
    /// attempts and then accept the caller's own connect.
    /// </summary>
    public bool RefuseConnects
    {
        get => Volatile.Read(ref _refuseConnects);
        set => Volatile.Write(ref _refuseConnects, value);
    }

    /// <summary>
    /// Runs inside <see cref="Create"/> for every socket, before the connection receives it, so a test can arm a socket
    /// (<see cref="PipedSocket.DuringFirstDispose"/>) that the connection creates and releases with no pause in between.
    /// </summary>
    public Action<PipedSocket>? OnCreated { get; set; }

    /// <summary>Every socket handed out so far, in creation order.</summary>
    public IReadOnlyList<PipedSocket> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _all];
            }
        }
    }

    public ISocketConnection Create()
    {
        PipedSocket socket;
        TaskCompletionSource<PipedSocket> created;
        lock (_gate)
        {
            socket = new PipedSocket(refusesConnect: _all.Count >= ConnectsAccepted || RefuseConnects);
            _all.Add(socket);
            created = _nextCreated;
            _nextCreated = NewCreatedSignal();
        }

        OnCreated?.Invoke(socket);

        _created.Writer.TryWrite(socket);
        created.TrySetResult(socket);
        return socket;
    }

    public ISocketConnection FromStream(Stream stream) =>
        throw new NotSupportedException("The AMI client only dials out.");

    /// <summary>The next socket the connection creates, in order; waits until it has been created.</summary>
    public ValueTask<PipedSocket> NextAsync(CancellationToken cancellationToken) =>
        _created.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// The first socket the connection creates after this call, if it creates one within
    /// <paramref name="window"/>; else <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is what is being observed, not a hang bound: a socket created inside it is returned,
    /// and <see langword="null"/> means none was. A test that asserts the connection stopped dialling
    /// pairs the window with a positive control, the same setup with nothing ending the connection,
    /// which does see a socket inside the same window.
    /// </para>
    /// <para>
    /// It only watches. It takes nothing from the queue <see cref="NextAsync"/> reads, so a peer task
    /// draining <see cref="NextAsync"/> at the same time still receives every socket. A socket created
    /// before the call is not returned, however long it has waited in that queue.
    /// </para>
    /// </remarks>
    public async Task<PipedSocket?> NextWithinAsync(TimeSpan window)
    {
        Task<PipedSocket> next;
        lock (_gate)
        {
            next = _nextCreated.Task;
        }

        using var windowCts = new CancellationTokenSource(window);
        try
        {
            return await next.WaitAsync(windowCts.Token);
        }
        catch (OperationCanceledException) when (windowCts.IsCancellationRequested)
        {
            return null;
        }
    }

    private static TaskCompletionSource<PipedSocket> NewCreatedSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// An in-memory socket whose far end the test drives as the Asterisk peer. It counts its disposals, so
/// a test can assert that the connection released it exactly once.
/// </summary>
/// <remarks>
/// <see cref="CloseAsync"/> and <see cref="DisposeAsync"/> end the stream the connection reads, as
/// <see cref="PipelineSocketConnection"/> does, so the connection's reader loop ends exactly as it does
/// when a real transport is torn down. The peer's writes and that ending share one lock: a peer that
/// answers while the socket closes loses the answer instead of racing the pipe.
/// </remarks>
internal sealed class PipedSocket(bool refusesConnect = false) : ISocketConnection
{
    private readonly Pipe _toConnection = new();
    private readonly Pipe _toPeer = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeCount;

    public bool IsConnected => !_closed.IsCancellationRequested;

    /// <summary>Whether this socket refuses its connect, as a peer that is down does.</summary>
    public bool RefusesConnect => refusesConnect;

    public PipeReader Input => _toConnection.Reader;

    public PipeWriter Output => _toPeer.Writer;

    /// <summary>How many times the connection has disposed this socket.</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Completes the first time the connection disposes this socket.</summary>
    public Task Disposed => _disposed.Task;

    /// <summary>
    /// Runs inside the first disposal, before it returns, so a test can make something happen while
    /// the connection is in the middle of releasing this socket. It is not awaited.
    /// </summary>
    public Action? DuringFirstDispose { get; set; }

    public ValueTask ConnectAsync(string hostname, int port, bool useSsl = false,
        CancellationToken cancellationToken = default) =>
        refusesConnect
            ? ValueTask.FromException(new SocketException((int)SocketError.ConnectionRefused))
            : ValueTask.CompletedTask;

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        Close();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        var first = Interlocked.Increment(ref _disposeCount) == 1;
        Close();
        _disposed.TrySetResult();
        if (first)
            DuringFirstDispose?.Invoke();

        return ValueTask.CompletedTask;
    }

    private void Close()
    {
        lock (_gate)
        {
            if (_closed.IsCancellationRequested)
                return;

            _closed.Cancel();
            _toConnection.Writer.Complete();
        }
    }

    // ── The peer's side ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The peer closes the socket: the connection's reader sees the stream end, as it does when Asterisk
    /// goes away. The connection still owns the socket and must dispose it.
    /// </summary>
    public void CloseFromPeer() => Close();

    /// <summary>
    /// The stream the connection reads fails with <paramref name="error"/>, as a transport that breaks mid-read does:
    /// the connection's next read throws it instead of seeing the stream end. The socket is closed as by
    /// <see cref="CloseFromPeer"/>; the connection still owns it and must dispose it.
    /// </summary>
    public void FaultFromPeer(Exception error)
    {
        lock (_gate)
        {
            if (_closed.IsCancellationRequested)
                return;

            _closed.Cancel();
            _toConnection.Writer.Complete(error);
        }
    }

    /// <summary>
    /// Reads the next action the connection wrote. Returns <see langword="null"/> once the socket has
    /// been closed; throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// fires first.
    /// </summary>
    public async Task<string?> ReadActionAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var reader = _toPeer.Reader;
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(linked.Token);
                var buffer = result.Buffer;
                var text = Encoding.UTF8.GetString(buffer);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    reader.AdvanceTo(buffer.GetPosition(end + 4));
                    return text[..(end + 4)];
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                    return null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes, without waiting, every complete action the connection wrote that the peer has not read.
    /// </summary>
    /// <remarks>
    /// Once the socket is closed, <see cref="ReadActionAsync"/> returns <see langword="null"/> without reading, so
    /// this is how a test sees what the connection still sent on a session that had already ended. It must not run
    /// while a <see cref="ReadActionAsync"/> is in flight: call it once the peer has stopped reading.
    /// </remarks>
    public IReadOnlyList<string> TakeUnreadActions()
    {
        var reader = _toPeer.Reader;
        if (!reader.TryRead(out var result))
            return [];

        var buffer = result.Buffer;
        var text = Encoding.UTF8.GetString(buffer);
        var actions = new List<string>();
        var consumed = 0;
        while (text.IndexOf("\r\n\r\n", consumed, StringComparison.Ordinal) is var end and >= 0)
        {
            actions.Add(text[consumed..(end + 4)]);
            consumed = end + 4;
        }

        reader.AdvanceTo(buffer.GetPosition(consumed), buffer.End);
        return actions;
    }

    /// <summary>
    /// Reads the next action if one arrives within <paramref name="window"/>, else <see langword="null"/>.
    /// The window is what is being observed: an action that arrives inside it is returned.
    /// </summary>
    public async Task<string?> ReadActionWithinAsync(TimeSpan window)
    {
        using var windowCts = new CancellationTokenSource(window);
        try
        {
            return await ReadActionAsync(windowCts.Token);
        }
        catch (OperationCanceledException) when (windowCts.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Answers an action. Returns <see langword="false"/> when the socket was already closed.</summary>
    public async Task<bool> RespondAsync(string status, string actionId,
        IEnumerable<KeyValuePair<string, string>>? fields = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Response: {status}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"ActionID: {actionId}\r\n");
        foreach (var kv in fields ?? [])
            sb.Append(CultureInfo.InvariantCulture, $"{kv.Key}: {kv.Value}\r\n");
        sb.Append("\r\n");
        return await WriteAsync(sb.ToString());
    }

    /// <summary>
    /// Sends an unsolicited event, as Asterisk does, for example <c>WriteEventAsync("FullyBooted")</c>.
    /// Returns <see langword="false"/> when the socket was already closed.
    /// </summary>
    public Task<bool> WriteEventAsync(string eventType, IEnumerable<KeyValuePair<string, string>>? fields = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Event: {eventType}\r\n");
        foreach (var kv in fields ?? [])
            sb.Append(CultureInfo.InvariantCulture, $"{kv.Key}: {kv.Value}\r\n");
        sb.Append("\r\n");
        return WriteAsync(sb.ToString());
    }

    /// <summary>Writes raw text to the connection. Returns <see langword="false"/> when the socket was already closed.</summary>
    public async Task<bool> WriteAsync(string text)
    {
        ValueTask<FlushResult> flush;
        lock (_gate)
        {
            if (_closed.IsCancellationRequested)
                return false;

            _toConnection.Writer.Write(Encoding.UTF8.GetBytes(text));
            flush = _toConnection.Writer.FlushAsync();
        }

        await flush;
        return true;
    }

    /// <summary>Plays the peer through the banner, the MD5 challenge, the login and the version probe.</summary>
    public async Task CompleteLoginAsync(CancellationToken cancellationToken)
    {
        await WriteAsync("Asterisk Call Manager/6.0.0\r\n");

        var challenge = await ReadActionAsync(cancellationToken) ?? throw ClosedDuringLogin();
        await RespondAsync("Success", ActionIdOf(challenge), [new("Challenge", "abc123")]);

        var login = await ReadActionAsync(cancellationToken) ?? throw ClosedDuringLogin();
        await RespondAsync("Success", ActionIdOf(login), [new("Message", "Authentication accepted")]);

        var coreSettings = await ReadActionAsync(cancellationToken) ?? throw ClosedDuringLogin();
        await RespondAsync("Success", ActionIdOf(coreSettings), [new("AsteriskVersion", "20.0.0")]);
    }

    /// <summary>Answers every Ping until the socket closes.</summary>
    public async Task AnswerPingsAsync(CancellationToken cancellationToken)
    {
        while (await ReadActionAsync(cancellationToken) is { } action)
        {
            if (IsPing(action))
                await RespondAsync("Success", ActionIdOf(action));
        }
    }

    public static bool IsPing(string? action) =>
        action is not null && action.StartsWith("Action: Ping\r\n", StringComparison.OrdinalIgnoreCase);

    public static string ActionIdOf(string action)
    {
        var match = Regex.Match(action, @"ActionID:\s*(.+?)\r?\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static InvalidOperationException ClosedDuringLogin() =>
        new("The connection closed the socket before the login finished.");
}

/// <summary>
/// A logger whose lines a test can wait on: <see cref="Logged"/> completes when a line containing the
/// fragment has been written, which makes a log line a signal instead of something to poll for.
/// </summary>
internal sealed class SignalingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Line)> _entries = [];
    private readonly List<(string Fragment, int Times, TaskCompletionSource Signal)> _waiters = [];

    /// <summary>Every line logged so far, with its level, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Line)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Completes once a line containing <paramref name="fragment"/> has been logged (already or later).</summary>
    public Task Logged(string fragment) => Logged(fragment, times: 1);

    /// <summary>
    /// Completes once <paramref name="times"/> lines containing <paramref name="fragment"/> have been logged, counting
    /// the ones already logged: the second <c>[LIVE] State loaded</c> is a reload's, when the first was the start's.
    /// </summary>
    public Task Logged(string fragment, int times)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
        lock (_gate)
        {
            if (CountLocked(fragment) >= times)
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((fragment, times, signal));
            return signal.Task;
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = formatter(state, exception);
        List<TaskCompletionSource> fired = [];
        lock (_gate)
        {
            _entries.Add((logLevel, line));
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var (fragment, times, signal) = _waiters[i];
                if (line.Contains(fragment, StringComparison.Ordinal) && CountLocked(fragment) >= times)
                {
                    fired.Add(signal);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var signal in fired)
            signal.TrySetResult();
    }

    private int CountLocked(string fragment) =>
        _entries.Count(entry => entry.Line.Contains(fragment, StringComparison.Ordinal));
}
