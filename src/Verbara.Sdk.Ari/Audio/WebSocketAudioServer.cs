using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Ari.Audio;

internal static partial class WebSocketAudioServerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[WebSocketAudio] Server started: port={Port}")]
    public static partial void ServerStarted(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "[WebSocketAudio] Server stopped")]
    public static partial void ServerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[WebSocketAudio] Connection accepted: remote={RemoteEndpoint} channel_id={ChannelId}")]
    public static partial void ConnectionAccepted(ILogger logger, string? remoteEndpoint, string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[WebSocketAudio] Connection error")]
    public static partial void ConnectionError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[WebSocketAudio] Invalid upgrade request")]
    public static partial void InvalidUpgrade(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "[WebSocketAudio] Accept failed — the server stays bound and accepts again after a backoff")]
    public static partial void AcceptLoopFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Listens for incoming WebSocket connections from Asterisk ExternalMedia channels.
/// Uses TcpListener + manual HTTP upgrade + WebSocket.CreateFromStream() (ADR-1).
/// </summary>
public sealed class WebSocketAudioServer : IAudioServer, IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>The wait after the first of a run of failed accepts.</summary>
    internal static readonly TimeSpan InitialAcceptBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait between failed accepts, however long the run.</summary>
    internal static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(5);

    private readonly AudioServerOptions _options;
    private readonly ILogger<WebSocketAudioServer> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, WebSocketAudioSession> _streams = new();
    private readonly Subject<IAudioStream> _streamSubject = new();
    // Connection handlers that have not finished. Each handler removes and disposes its own session
    // on its way out, so StopAsync waits for these until its token is cancelled.
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _running;

    /// <summary>Observable that emits each new audio stream when a connection is established.</summary>
    public IObservable<IAudioStream> OnStreamConnected => _streamSubject;

    /// <summary>
    /// Get an active stream by the last path segment of the HTTP upgrade request URL it arrived on,
    /// query string stripped — not by an ARI channel id, and not by the AudioSocket identification
    /// UUID. See <see cref="IAudioServer.GetStream(string)"/>.
    /// </summary>
    public IAudioStream? GetStream(string channelId) =>
        _streams.TryGetValue(channelId, out var session) ? session : null;

    /// <summary>All currently active audio streams.</summary>
    public IEnumerable<IAudioStream> ActiveStreams => _streams.Values;

    /// <summary>Number of currently active audio streams.</summary>
    public int ActiveStreamCount => _streams.Count;

    /// <summary>
    /// Whether the server is bound and accepting. Reads <c>false</c> from the moment a stop
    /// <em>begins</em>, not from the moment its teardown finishes, and is published through a
    /// barrier so the accept loop's thread cannot read a stale value.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public WebSocketAudioServer(AudioServerOptions options, ILogger<WebSocketAudioServer> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance whose accept backoff waits on <paramref name="timeProvider"/>, so a
    /// test can drive that wait with a fake clock instead of sitting out a real one.
    /// </summary>
    internal WebSocketAudioServer(
        AudioServerOptions options,
        ILogger<WebSocketAudioServer> logger,
        TimeProvider timeProvider)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Replaces the listener's accept when set. Settable by tests (via InternalsVisibleTo), so a test
    /// can make accepts fail on demand instead of exhausting file descriptors to get a failure.
    /// </summary>
    internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride { get; set; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        // First, and before anything is built: a second start that got as far as replacing _cts
        // would leave the first accept loop running against a source nothing can cancel, and the
        // listener, the source and the loop task it overwrote all leaked.
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return ValueTask.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.WebSocketPort);
        _listener.Start();

        WebSocketAudioServerLog.ServerStarted(_logger, _options.WebSocketPort);

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var backoff = InitialAcceptBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await AcceptAsync(ct);
                backoff = InitialAcceptBackoff;
                client.NoDelay = true;

                if (_streams.Count >= _options.MaxConcurrentStreams)
                {
                    client.Dispose();
                    continue;
                }

                // Started inline on the loop's own continuation and not through `Task.Run`, which is
                // how this server has always handed a connection over and is left unchanged: `ct`
                // travels with it as an argument, so there is no scheduler hand-off for CA2016 to
                // forward a token to. Tracking it is what lets `StopAsync` wait for the handler.
                TrackConnection(HandleConnectionAsync(client, ct));
                continue;
            }
            catch (OperationCanceledException)
            {
                // The stop path. `ct` is `_cts.Token`, cancelled by `StopAsync` and so by
                // `DisposeAsync`, and the pending accept ended with it. Nothing is meant to be
                // accepted after that, so the loop ends rather than backing off.
                break;
            }
            catch (ObjectDisposedException)
            {
                // The Windows shape of that same stop: `Stop()` disposes the underlying socket under
                // a pending accept and the accept surfaces the disposal. NOT reached on Linux, where
                // `Stop()` on a pending accept raises `SocketException(OperationAborted)` and the
                // filtered catch below takes it instead — this block is live on Windows, not dead
                // code.
                break;
            }
            catch (SocketException) when (!IsRunning)
            {
                // The Linux shape of that same stop, and the arm `StopAsync` had to be reordered
                // before it could exist: `StopAsync` now clears `_running` BEFORE it calls `Stop()`,
                // so an accept aborted by that `Stop()` always arrives with `IsRunning` already
                // false. Measured on a real loopback listener, this is the shape 7 stops in 10 take,
                // against 3 in 10 for the `OperationCanceledException` above — so without this arm a
                // routine shutdown would be reported as an accept failure most of the time.
                // `IsRunning` is the discriminator and not `ct`, because `StopAsync` cancels `_cts`
                // only after `Stop()` returns — a filter on `ct.IsCancellationRequested` would race
                // that ordering.
                break;
            }
            catch (SocketException ex)
            {
                // An accept that failed while the server is still meant to be running: EMFILE/ENFILE,
                // a connection aborted in the backlog, ENOBUFS. Ending the loop here would leave
                // `IsRunning` reporting true over a socket still in LISTEN — the kernel would keep
                // completing handshakes nobody reads, so Asterisk's ExternalMedia channels would hang
                // instead of being refused, and `StartAsync` could not restart the loop because
                // `_running` is already 1. So it is logged and the loop keeps accepting.
                WebSocketAudioServerLog.AcceptLoopFailed(_logger, ex);
            }

            // A failure that persists fails every accept at once, and without a pause this loop would
            // spin and log without bound. The wait doubles with each consecutive failure up to the
            // cap, and a successful accept above starts it over.
            //
            // This await does a second job that is easy to miss, and removing the wait breaks it:
            // StartAsync starts this loop by calling it, not through Task.Run, so the loop runs on the
            // caller's thread until its first suspension point. In normal operation that is the accept
            // itself, which yields at once. On the failure path this delay is the only one — without it
            // a persistent failure spins INSIDE StartAsync instead of behind it, and StartAsync never
            // returns. Measured: the mutation that deletes this wait pins a test host at 98% CPU rather
            // than going red, which in CI reads as a job timeout instead of a failed test. If this wait
            // is ever moved or removed, start the loop with Task.Run first.
            try
            {
                await Task.Delay(backoff, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                // Same stop path as above, caught while waiting out a backoff rather than while
                // accepting: `_cts` was cancelled by `StopAsync`. The loop ends.
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
        }
    }

    private ValueTask<TcpClient> AcceptAsync(CancellationToken ct) =>
        AcceptOverride?.Invoke(ct) ?? _listener!.AcceptTcpClientAsync(ct);

    /// <summary>
    /// Keeps a connection handler in <see cref="_connections"/> until it completes. The handler is
    /// added before the removal is attached, and a continuation attached to a task that has already
    /// completed runs at once, so a handler that finished synchronously is still removed.
    /// </summary>
    private void TrackConnection(Task connection)
    {
        _connections.TryAdd(connection, 0);
        _ = connection.ContinueWith(
            static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
            _connections,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            WebSocketAudioSession? session = null;
            try
            {
                var stream = client.GetStream();

                // Read HTTP upgrade request
                var (wsKey, channelId) = await ReadUpgradeRequestAsync(stream, ct);
                if (wsKey is null || channelId is null)
                {
                    WebSocketAudioServerLog.InvalidUpgrade(_logger);
                    return;
                }

                // Send HTTP 101 response
                await SendUpgradeResponseAsync(stream, wsKey, ct);

                // Create ManagedWebSocket
                var webSocket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });

                session = new WebSocketAudioSession(webSocket, channelId, _options.DefaultFormat);
                session.Start();

                var endpoint = client.Client.RemoteEndPoint?.ToString();
                WebSocketAudioServerLog.ConnectionAccepted(_logger, endpoint, channelId);

                _streams.TryAdd(channelId, session);
                _streamSubject.OnNext(session);

                // Wait for session to disconnect
                var tcs = new TaskCompletionSource();
                using var sub = session.StateChanges.Subscribe(state =>
                {
                    if (state is AudioStreamState.Disconnected or AudioStreamState.Error)
                        tcs.TrySetResult();
                });

                if (!session.IsConnected)
                    tcs.TrySetResult();

                await tcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // The stop token, and only that. Unlike the AudioSocket server's counterpart this
                // method schedules no idle deadline, so no second source is linked into `ct` and no
                // second ending arrives here. `ct` is `_cts.Token`, cancelled by `StopAsync` and
                // `DisposeAsync`; every await in the `try` is the upgrade request, the 101 response or
                // the wait for the session to disconnect, each under that token. The `finally` still
                // deregisters and disposes the session, and the enclosing `using (client)` still
                // closes the connection.
                /* Best effort — the server is stopping */
            }
            catch (Exception ex)
            {
                WebSocketAudioServerLog.ConnectionError(_logger, ex);
            }
            finally
            {
                // Each connection cleans up its own session and no other. The pair overload removes
                // the entry only while it still maps to this session, so a connection that lost the
                // TryAdd race for a channel id leaves the session that won it registered.
                if (session is not null)
                {
                    _streams.TryRemove(new KeyValuePair<string, WebSocketAudioSession>(session.ChannelId, session));
                    await session.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// Read HTTP upgrade request headers, extract Sec-WebSocket-Key and channel ID from URL path.
    /// Expected URL: /ws/{channelId} or /{channelId}
    /// </summary>
    internal static async Task<(string? wsKey, string? channelId)> ReadUpgradeRequestAsync(
        Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var totalRead = 0;

        // Read until we get the full HTTP headers (double CRLF)
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (bytesRead == 0) return (null, null);
            totalRead += bytesRead;

            if (Encoding.ASCII.GetString(buffer, 0, totalRead).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        var request = Encoding.ASCII.GetString(buffer, 0, totalRead);
        var lines = request.Split("\r\n");
        if (lines.Length == 0) return (null, null);

        // Parse request line: GET /ws/{channelId} HTTP/1.1
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return (null, null);

        var path = requestLine[1];
        var channelId = path.TrimStart('/').Split('/').LastOrDefault()?.Split('?').FirstOrDefault();

        string? wsKey = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                wsKey = line["Sec-WebSocket-Key:".Length..].Trim();
                break;
            }
        }

        return (wsKey, channelId);
    }

    /// <summary>Send HTTP 101 Switching Protocols response.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SHA1 is required by RFC 6455 WebSocket protocol")]
    internal static async Task SendUpgradeResponseAsync(Stream stream, string wsKey, CancellationToken ct)
    {
        var acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + WebSocketGuid)));

        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Stops accepting connections, cancels the connection handlers and waits for them to finish,
    /// which disposes their sessions. Cancelling <paramref name="cancellationToken"/> ends that wait,
    /// and the sessions still registered are then disposed directly.
    /// </summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        // The running flag goes down FIRST, before anything that can abort a pending accept. The
        // accept loop tells a stop from a failure by reading it, so an abort that reached the loop
        // while the flag was still up would have the loop reporting the server's own shutdown as a
        // failure. Clearing it here also makes a second stop — or a DisposeAsync racing this one —
        // a no-op rather than a second teardown, and it no longer depends on the two SuppressThrowing
        // waits below to be reached at all.
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        _listener?.Stop();

        if (_cts is not null)
            await _cts.CancelAsync();

        // Both waits below end when the token is cancelled. An OnStreamConnected subscriber that
        // blocks holds its handler, and a handler whose upgrade request was already buffered runs on
        // the accept loop until it reaches that subscriber, so either wait can be the one held.
        if (_acceptLoop is not null)
            await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Once the accept loop has ended, every handler it started is tracked and no new one can
        // start. Cancellation has reached them; each one removes and disposes its own session in its
        // finally, so this wait is what makes a still-connected session disposed by the time
        // StopAsync returns.
        await Task.WhenAll(_connections.Keys).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Empty unless a wait was cancelled. A session disposed here makes its handler's own dispose a
        // no-op, and a handler that registers a session after this point still disposes it, because
        // its token is already cancelled.
        foreach (var session in _streams.Values)
            await session.DisposeAsync();
        _streams.Clear();

        WebSocketAudioServerLog.ServerStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) await StopAsync();
        _streamSubject.OnCompleted();
        _streamSubject.Dispose();
        _cts?.Dispose();
    }
}
