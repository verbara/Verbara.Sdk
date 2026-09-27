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

    /// <summary>
    /// How many sockets accept their connect. Every socket created after them refuses it, as a peer that
    /// is down does, so a test can make every reconnect fail. Unlimited by default.
    /// </summary>
    public int ConnectsAccepted { get; init; } = int.MaxValue;

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
        lock (_gate)
        {
            socket = new PipedSocket(refusesConnect: _all.Count >= ConnectsAccepted);
            _all.Add(socket);
        }

        _created.Writer.TryWrite(socket);
        return socket;
    }

    public ISocketConnection FromStream(Stream stream) =>
        throw new NotSupportedException("The AMI client only dials out.");

    /// <summary>The next socket the connection creates, in order; waits until it has been created.</summary>
    public ValueTask<PipedSocket> NextAsync(CancellationToken cancellationToken) =>
        _created.Reader.ReadAsync(cancellationToken);
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
    private readonly List<string> _lines = [];
    private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

    /// <summary>Completes once a line containing <paramref name="fragment"/> has been logged (already or later).</summary>
    public Task Logged(string fragment)
    {
        lock (_gate)
        {
            if (_lines.Exists(line => line.Contains(fragment, StringComparison.Ordinal)))
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((fragment, signal));
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
            _lines.Add(line);
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
    }
}
