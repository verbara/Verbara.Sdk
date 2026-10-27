using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Diagnostics;
using Verbara.Sdk.Ari.Internal;

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "[WebSocketAudio] Stream limit reached ({Limit}), rejecting connection")]
    public static partial void StreamLimitReached(ILogger logger, int limit);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[WebSocketAudio] Connection announced under its path segment {PathSegment}: its first frame is binary, not MEDIA_START")]
    public static partial void ConnectionUnnamedByBinaryFirstFrame(ILogger logger, string pathSegment);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[WebSocketAudio] Connection announced under its path segment {PathSegment}: its first frame is the text frame {Event}, not MEDIA_START")]
    public static partial void ConnectionUnnamedByTextFirstFrame(ILogger logger, string pathSegment, string @event);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[WebSocketAudio] Connection announced under its path segment {PathSegment}: its first frame is a MEDIA_START without a channel_id")]
    public static partial void ConnectionUnnamedByMediaStartWithoutChannelId(ILogger logger, string pathSegment);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[WebSocketAudio] Connection announced under its path segment {PathSegment}: it sent no frame within MediaStartTimeout ({MediaStartTimeout})")]
    public static partial void ConnectionUnnamedByMediaStartTimeout(ILogger logger, string pathSegment, TimeSpan mediaStartTimeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[WebSocketAudio] A connection's path segment {PathSegment} equals the channel_id {ChannelId} another connection named in its MEDIA_START: the connection that named the call holds the key, and the other is listed and counted but not found under it")]
    public static partial void PathSegmentCollision(ILogger logger, string pathSegment, string channelId);
}

/// <summary>
/// Listens for the WebSocket media connections Asterisk's chan_websocket makes through a
/// <c>websocket_client</c> connection: from an ARI <c>externalMedia</c> channel created with
/// <c>transport=websocket</c>, and from a dialplan <c>Dial(WebSocket/&lt;connection&gt;)</c>. Each stream is keyed
/// by the call its first frame, <c>MEDIA_START</c>, names.
/// The server owns a <see cref="TcpListener"/> and answers the HTTP upgrade itself, then wraps the upgraded stream
/// with <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>: it needs no ASP.NET Core host,
/// so it runs inside any process, Native AOT included, and it reads the upgrade's path, which carries the key of a
/// connection that names no call.
/// </summary>
public sealed class WebSocketAudioServer : IAudioServer, IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>The wait after the first of a run of failed accepts.</summary>
    internal static readonly TimeSpan InitialAcceptBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait between failed accepts, however long the run.</summary>
    internal static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(5);

    /// <summary>The longest <see cref="AudioServerOptions.MediaStartTimeout"/> a timer can be armed for.</summary>
    internal static readonly TimeSpan MaxMediaStartTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    /// <summary>The subprotocol Asterisk's <c>websocket_client</c> offers, and the 101 selects when offered.</summary>
    internal const string MediaSubprotocol = "media";

    private readonly AudioServerOptions _options;
    private readonly ILogger<WebSocketAudioServer> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, WebSocketAudioSession> _streams = new();
    // Every announced connection still open: the holder of each key and each connection waiting for a
    // key another one holds. ActiveStreams and ActiveStreamCount read it, so a waiting connection is
    // listed and counted like any other live stream.
    // A connection enters it pending, before the stop is read a second time, and is announced only once
    // admitted; the stop claims a pending entry for its registrant to withdraw.
    private readonly ConcurrentDictionary<WebSocketAudioSession, RegistryEntry> _live = new();
    private readonly Subject<IAudioStream> _streamSubject = new();
    // Connection handlers that have not finished. Each handler removes and disposes its own session
    // on its way out, so StopAsync waits for these until its token is cancelled.
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _running;
    private int _disposed;

    // The bound the running server waits on, read from the options and validated by StartAsync.
    private TimeSpan _mediaStartTimeout;

    /// <summary>Observable that emits each new audio stream when a connection is established.</summary>
    public IObservable<IAudioStream> OnStreamConnected => _streamSubject;

    /// <summary>
    /// Get an active stream by the call its <c>MEDIA_START</c> named: the literal <c>channel_id</c> of the
    /// connection's first frame, compared ordinally. For an <c>externalMedia</c> channel that is the ARI channel id
    /// the create returned; for a <c>Dial(WebSocket/…)</c> it is the uniqueid of the WebSocket channel, not the
    /// caller's. A connection whose first frame is not <c>MEDIA_START</c>, or that sends nothing within
    /// <see cref="AudioServerOptions.MediaStartTimeout"/>, is keyed by the last path segment of its upgrade request,
    /// query string stripped.
    /// </summary>
    /// <remarks>
    /// When several live connections present one key, this returns the earliest of them still connected in the
    /// highest tier present: a connection that names the call outranks one that only presents the value as its path
    /// segment. The others are announced on <see cref="OnStreamConnected"/>, listed and counted, but not found until
    /// they take the key over. A connection still waiting for its first frame is not found. See
    /// <see cref="IAudioServer.GetStream(string)"/>.
    /// </remarks>
    public IAudioStream? GetStream(string channelId) =>
        _streams.TryGetValue(channelId, out var session) ? session : null;

    /// <summary>
    /// All currently active audio streams: every connection announced on
    /// <see cref="OnStreamConnected"/> that has not ended, including one that waits for a path segment
    /// another live connection holds. chan_websocket upgrades every call of one websocket_client
    /// connection on the same URI, so several entries can report the same
    /// <see cref="IAudioStream.ChannelId"/>.
    /// </summary>
    public IEnumerable<IAudioStream> ActiveStreams => _live.Keys;

    /// <summary>
    /// Number of currently active audio streams: one per live announced connection, whatever path it
    /// upgraded on, so it counts calls and not distinct URIs.
    /// </summary>
    public int ActiveStreamCount => _live.Count;

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

    /// <summary>
    /// Runs on a connection's handler just before the server registers it. Settable by tests (via
    /// InternalsVisibleTo), so a test can run a stop inside that window instead of hoping a race lands
    /// there.
    /// </summary>
    internal Action? BeforeRegistration { get; set; }

    /// <summary>
    /// Runs on a connection's handler right after its entry was added to the server's tables and before
    /// it is announced. Settable by tests (via InternalsVisibleTo), for the same reason.
    /// </summary>
    internal Action? AfterRegistryAdd { get; set; }

    /// <summary>
    /// Binds the listener and starts the accept loop. A start while the server is running completes and
    /// binds nothing. The server serves until <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Unused: this start begins nothing that can be aborted.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> is negative, infinite, or longer than a timer can be armed
    /// for. Nothing is bound.
    /// </exception>
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        // There is no validator for AudioServerOptions, so the bound is checked here, before anything binds: a
        // value no timer can honour would fail every connection's wait instead of the start.
        var mediaStartTimeout = _options.MediaStartTimeout;
        if (mediaStartTimeout < TimeSpan.Zero || mediaStartTimeout > MaxMediaStartTimeout)
        {
#pragma warning disable CA2208, MA0015 // The value at fault is the option this start reads, named as the consumer sets it.
            throw new ArgumentOutOfRangeException(
                nameof(AudioServerOptions.MediaStartTimeout),
                mediaStartTimeout,
                "MediaStartTimeout must be zero (no wait) or a positive duration a timer can be armed for; " +
                "a negative or infinite value cannot be honoured.");
#pragma warning restore CA2208, MA0015
        }

        // The parameter is deliberately unused: it means the start was aborted, and this start begins
        // nothing that can be aborted. The accept loop and every connection handler get the token this
        // server owns, which the server's own stop cancels; a source linked to the start's token would
        // end them all when that token is cancelled after the start returned.
        //
        // First, and before anything is built: a second start that got as far as replacing _cts
        // would leave the first accept loop running against a source nothing can cancel, and the
        // listener, the source and the loop task it overwrote all leaked.
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return ValueTask.CompletedTask;

        // A start that throws after the exchange — an invalid ListenAddress, a port another listener
        // holds — gives the running flag back and leaves no listener, so the server reports itself
        // stopped and a later start binds as a first one does. The source is built only once the
        // listener is bound, so a failed start has nothing else to release.
        var started = false;
        try
        {
            _listener = BoundListener.Start(_options.ListenAddress, _options.WebSocketPort);
            started = true;
        }
        finally
        {
            if (!started)
                Volatile.Write(ref _running, 0);
        }

        _cts = new CancellationTokenSource();
        _mediaStartTimeout = mediaStartTimeout;

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

                // Nothing that can fail for this one connection runs between the accept and the
                // hand-off, not even configuring the socket; the capacity check only takes a place
                // from a count, and closes and reports what it refuses. Configuring fails in the handler, which closes the
                // connection and reports it as that connection's error. Here it would reach the catches
                // below, which classify by type only: a SocketException would be logged as an accept
                // failure and backed off for, with the socket left open, and an
                // ObjectDisposedException would end the loop as if the server had been stopped.
                //
                // The place is taken here, at the accept and before the upgrade is read, from the count
                // every server built with these options shares, so a connection that has not sent its
                // upgrade yet, or that will wait for a held key, is bounded like any other. The handler
                // gives it back when it ends.
                if (!_options.Admission.TryEnter(_options.MaxConcurrentStreams))
                {
                    client.Dispose();

                    // Reported after the close, so a logger or listener that throws cannot leave the
                    // refused socket open, and from nothing read off the connection. A refusal decided
                    // once the stop has begun is the stop, not a full server: the running flag tells
                    // them apart, as in the SocketException arm below, because StopAsync clears it
                    // before it cancels `ct`. The count goes first: a MeterListener runs inside Add, so
                    // whoever has seen the Warning has seen the count.
                    if (IsRunning)
                    {
                        AudioStreamMetrics.ConnectionsRefused.Add(1, AudioStreamMetrics.WebSocketTransport);
                        WebSocketAudioServerLog.StreamLimitReached(_logger, _options.MaxConcurrentStreams);
                    }

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

    // Several live connections can present one key: two connections can name one call (Asterisk does not
    // reject a duplicate channel_id), and connections that name no call share their path segment, which is
    // the same for every call of one websocket_client connection. There is one keyspace and two tiers:
    // connections that named the call in their MEDIA_START, then connections that only present the value as
    // their path segment. The holder is the earliest live connection of the highest tier present. Each other
    // one is announced but waits here, unregistered, ordered by (tier, arrival), and takes the key over when
    // the connections ahead of it have ended. A named connection takes the key from an unnamed holder, which
    // returns to the head of its own tier. Every waiter is still listed and counted: ActiveStreams and
    // ActiveStreamCount read _live, which holds every announced connection. The gate orders Register's retry
    // against every Release. Lookups read _streams, and neither they nor the counts take it.
    private readonly Lock _registryGate = new();
    private readonly Dictionary<string, List<WebSocketAudioSession>> _waiting = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="session"/> under <paramref name="id"/>, or, while another live
    /// connection holds that id, places it in the id's waiting list by (tier, arrival). A session that
    /// named the call takes the id from a holder that only presents it as its path segment. The
    /// uncontended path takes no lock.
    /// </summary>
    private void Register(string id, WebSocketAudioSession session)
    {
        if (_streams.TryAdd(id, session))
            return;

        bool collision;
        lock (_registryGate)
        {
            while (true)
            {
                // The holder may have been released between the first attempt and the lock, or between two turns.
                if (_streams.TryAdd(id, session))
                    return;
                if (!_streams.TryGetValue(id, out var holder))
                    continue;

                if (!_waiting.TryGetValue(id, out var waiters))
                    _waiting[id] = waiters = [];

                if (session.IsNamed && !holder.IsNamed)
                {
                    // One atomic replace, as in Release; it fails only when the entry moved since the read above,
                    // and the next turn reads it again.
                    if (!_streams.TryUpdate(id, session, holder))
                        continue;

                    // The displaced holder goes back ahead of every other connection of its tier. No named
                    // connection can be waiting here: one would have displaced this holder at its own turn.
                    waiters.Insert(NamedCount(waiters), holder);
                    collision = true;
                    break;
                }

                waiters.Insert(session.IsNamed ? NamedCount(waiters) : waiters.Count, session);
                collision = holder.IsNamed && !session.IsNamed;
                break;
            }
        }

        if (collision)
            WebSocketAudioServerLog.PathSegmentCollision(_logger, ChanWebSocketMediaStartParser.ForLog(id), ChanWebSocketMediaStartParser.ForLog(id));
    }

    /// <summary>The number of named sessions at the head of a waiting list: where the unnamed tier begins.</summary>
    private static int NamedCount(List<WebSocketAudioSession> waiters)
    {
        var count = 0;
        while (count < waiters.Count && waiters[count].IsNamed)
            count++;
        return count;
    }

    /// <summary>
    /// Releases what <paramref name="session"/> holds under <paramref name="id"/>: its place in the
    /// waiting list, or the entry itself. A held entry passes to the first waiting connection still
    /// connected, in (tier, arrival) order, or is removed when none is left. An entry that maps to another session is neither
    /// removed nor handed over.
    /// </summary>
    private void Release(string id, WebSocketAudioSession session)
    {
        lock (_registryGate)
        {
            if (_waiting.TryGetValue(id, out var waiters))
            {
                waiters.Remove(session);

                // A waiter that has already disconnected is never handed the id. Its own release,
                // still to come, then finds nothing to do.
                while (waiters.Count > 0 && !waiters[0].IsConnected)
                    waiters.RemoveAt(0);
            }

            if (waiters is { Count: > 0 })
            {
                // One atomic replace, so a lookup never finds the id empty while a connection that
                // presented it is open, and a newcomer's lock-free TryAdd cannot overtake the waiter.
                // It succeeds only while the entry still maps to this session: it fails when this
                // session was itself waiting, or when StopAsync has already cleared the table, so
                // nothing is handed over after a stop.
                if (_streams.TryUpdate(id, waiters[0], session))
                    waiters.RemoveAt(0);
            }
            else
            {
                // By value: the pair overload removes the entry only while it maps to this session.
                _streams.TryRemove(new KeyValuePair<string, WebSocketAudioSession>(id, session));
            }

            if (waiters is { Count: 0 })
                _waiting.Remove(id);
        }
    }

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
        try
        {
            await ServeConnectionAsync(client, ct);
        }
        finally
        {
            // The place the accept loop took for this connection, given back once on every path.
            _options.Admission.Exit();
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            WebSocketAudioSession? session = null;
            try
            {
                // Configuring the socket and obtaining its stream are part of serving this connection,
                // so they come first in this try: a failure of either (a socket that rejects the option,
                // one already closed, one that is not connected) is reported once below as this
                // connection's error, the finally finds no session, and the `using` closes the client.
                client.NoDelay = true;
                var stream = client.GetStream();

                // Read HTTP upgrade request, within IdleTimeout. The connection holds a place in the
                // shared admission count from its accept, so one that never sends its upgrade must give
                // it back, as the AudioSocket server's identification wait already does.
                (string? wsKey, string? channelId, bool offersMedia) upgrade;
                using (var idle = new CancellationTokenSource(_options.IdleTimeout, _timeProvider))
                using (var upgradeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token))
                {
                    try
                    {
                        upgrade = await ReadUpgradeAsync(stream, upgradeCts.Token);
                    }
                    catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        // No upgrade within IdleTimeout: the `using (client)` closes the connection
                        // and the handler gives its place back.
                        return;
                    }
                }

                var (wsKey, pathSegment, offersMedia) = upgrade;
                if (wsKey is null || pathSegment is null)
                {
                    WebSocketAudioServerLog.InvalidUpgrade(_logger);
                    return;
                }

                // Send HTTP 101 response, selecting the media subprotocol when the upgrade offered it. The offer
                // is only echoed: it is operator configuration, so it decides nothing about the key.
                await SendUpgradeResponseAsync(stream, wsKey, offersMedia ? MediaSubprotocol : null, ct);

                // Create ManagedWebSocket
                var webSocket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });

                session = new WebSocketAudioSession(webSocket, pathSegment, _options.DefaultFormat, _logger);
                session.Start();

                // The wait to be named. It sits after the session starts, so its pump reads the first frame, and
                // before the registration, so the session is re-keyed before anything can find or announce it.
                // The place taken at the accept is held through it, and nothing lists or counts the connection
                // until it is announced. It runs on the server's own token, never the start's.
                if (_mediaStartTimeout > TimeSpan.Zero)
                {
                    var first = await AwaitFirstFrameAsync(session, ct);
                    switch (first?.Kind)
                    {
                        case WebSocketFirstFrameKind.Ended:
                            // The peer closed or reset before its first frame. Its ending has been processed, so
                            // it is never registered, listed, counted or announced; the finally below disposes
                            // it, and the place goes back when this handler returns.
                            return;
                        case WebSocketFirstFrameKind.MediaStart:
                            session.Rekey(first.Value.Value!);
                            break;
                        default:
                            // Unnamed: a binary first frame, a text one that is not MEDIA_START, or the bound.
                            LogUnnamed(first, pathSegment);
                            break;
                    }
                }

                var key = session.ChannelId;
                var endpoint = client.Client.RemoteEndPoint?.ToString();
                WebSocketAudioServerLog.ConnectionAccepted(_logger, endpoint, key);

                // The stop is read before the registration and again after the add, as the Voice AI
                // server does. A connection whose stop has begun is refused: closed, never announced or
                // counted on audio.connections.refused, and nothing is logged. Its session has started, so
                // its disposal counts audio.streams.closed.
                BeforeRegistration?.Invoke();
                if (ct.IsCancellationRequested)
                    return;

                var entry = new RegistryEntry();
                Register(key, session);
                _live.TryAdd(session, entry);
                AfterRegistryAdd?.Invoke();

                // The add must be visible before the stop is read, or this read and the stop's walk of
                // _live could each miss the other's write. A stop that reaches the entry while it is
                // pending leaves it to this method, which withdraws it here.
                Interlocked.MemoryBarrier();
                if (ct.IsCancellationRequested || !entry.TryAdmit())
                    return;

                _streamSubject.OnNext(session);

                // The session completes this signal when its ending is published, before any consumer
                // observer is notified, so the release does not depend on what those observers do.
                await session.Ended.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // The stop token, and only that. Unlike the AudioSocket server's counterpart this
                // method schedules no idle deadline, so no second source is linked into `ct` and no
                // second ending arrives here. `ct` is `_cts.Token`, cancelled by `StopAsync` and
                // `DisposeAsync`; every await in the `try` is the upgrade request, the 101 response, the
                // wait for the first frame or the wait for the session to disconnect, each under that token.
                // A connection the stop reaches during its wait for the first frame is never announced. The `finally` still
                // deregisters and disposes the session, and the enclosing `using (client)` still
                // closes the connection.
                /* Best effort — the server is stopping */
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                // An announcement that met the subject a racing DisposeAsync released: that is the
                // stop, not a connection error. A consumer's own handler that throws
                // ObjectDisposedException while the server runs still reaches the catch below.
                /* Best effort — the server is being disposed */
            }
            catch (Exception ex)
            {
                WebSocketAudioServerLog.ConnectionError(_logger, ex);
            }
            finally
            {
                // Each connection cleans up its own session and no other. The pair overload removes
                // the entry only while it still maps to this session, so a connection that lost the
                // TryAdd race for a channel id leaves the session that won it registered. Release
                // keeps that rule and adds the hand-over: a holder that ends passes the id to the
                // earliest connection still open that presented it, and a connection that was
                // waiting for the id only leaves the waiting list.
                if (session is not null)
                {
                    _live.TryRemove(session, out _);
                    Release(session.ChannelId, session);
                    await session.DisposeAsync();
                }
            }
        }
    }

    /// <summary>Records at Debug why a connection is announced under its path segment.</summary>
    private void LogUnnamed(WebSocketFirstFrame? first, string pathSegment)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var segment = ChanWebSocketMediaStartParser.ForLog(pathSegment);
            if (first is not { } frame)
                WebSocketAudioServerLog.ConnectionUnnamedByMediaStartTimeout(_logger, segment, _mediaStartTimeout);
            else if (frame.Kind == WebSocketFirstFrameKind.Binary)
                WebSocketAudioServerLog.ConnectionUnnamedByBinaryFirstFrame(_logger, segment);
            else if (string.Equals(frame.Value, ChanWebSocketTextFrameHead.MediaStartEvent, StringComparison.Ordinal))
                WebSocketAudioServerLog.ConnectionUnnamedByMediaStartWithoutChannelId(_logger, segment);
            else
            {
                var eventName = ChanWebSocketMediaStartParser.ForLog(frame.Value);
                WebSocketAudioServerLog.ConnectionUnnamedByTextFirstFrame(_logger, segment, eventName);
            }
        }
    }

    /// <summary>
    /// Waits for <paramref name="session"/>'s first frame, bounded by
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> and by the server's stop. The bound elapsing is reported
    /// as <see langword="null"/>; the stop throws <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<WebSocketFirstFrame?> AwaitFirstFrameAsync(WebSocketAudioSession session, CancellationToken ct)
    {
        var firstFrame = session.FirstFrame;
        if (!firstFrame.IsCompleted)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var bound = Task.Delay(_mediaStartTimeout, _timeProvider, wait.Token);
            await Task.WhenAny(firstFrame, bound).ConfigureAwait(false);

            // Disarms the bound's timer when the frame came first.
            await wait.CancelAsync().ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return firstFrame.IsCompleted ? await firstFrame.ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Read HTTP upgrade request headers, extract Sec-WebSocket-Key and the last segment of the request path,
    /// query string stripped. The path segment is the key of a connection whose first frame names no call;
    /// Asterisk's <c>websocket_client</c> upgrades every call of one connection on the same fixed URI, so it is
    /// not where a call is named.
    /// </summary>
    internal static async Task<(string? wsKey, string? channelId)> ReadUpgradeRequestAsync(
        Stream stream, CancellationToken ct)
    {
        var (wsKey, channelId, _) = await ReadUpgradeAsync(stream, ct);
        return (wsKey, channelId);
    }

    /// <summary>
    /// <see cref="ReadUpgradeRequestAsync"/>, plus whether the upgrade offers the <c>media</c> subprotocol in
    /// <c>Sec-WebSocket-Protocol</c>.
    /// </summary>
    internal static async Task<(string? wsKey, string? channelId, bool offersMedia)> ReadUpgradeAsync(
        Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var totalRead = 0;

        // Read until we get the full HTTP headers (double CRLF)
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (bytesRead == 0) return (null, null, false);
            totalRead += bytesRead;

            if (Encoding.ASCII.GetString(buffer, 0, totalRead).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        var request = Encoding.ASCII.GetString(buffer, 0, totalRead);
        var lines = request.Split("\r\n");
        if (lines.Length == 0) return (null, null, false);

        // The request line: GET /<path>/<segment> HTTP/1.1. Asterisk's websocket_client sends the URI its
        // connection entry configures, the same for every call of that entry.
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return (null, null, false);

        var path = requestLine[1];
        var channelId = path.TrimStart('/').Split('/').LastOrDefault()?.Split('?').FirstOrDefault();

        string? wsKey = null;
        var offersMedia = false;
        foreach (var line in lines)
        {
            if (wsKey is null && line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                wsKey = line["Sec-WebSocket-Key:".Length..].Trim();
            }
            else if (line.StartsWith("Sec-WebSocket-Protocol:", StringComparison.OrdinalIgnoreCase)
                     // A comma-separated list, and the header may repeat (RFC 6455 §4.1).
                     && line["Sec-WebSocket-Protocol:".Length..].Split(',')
                         .Any(offered => string.Equals(offered.Trim(), MediaSubprotocol, StringComparison.OrdinalIgnoreCase)))
            {
                offersMedia = true;
            }
        }

        return (wsKey, channelId, offersMedia);
    }

    /// <summary>Send HTTP 101 Switching Protocols response, selecting no subprotocol.</summary>
    internal static Task SendUpgradeResponseAsync(Stream stream, string wsKey, CancellationToken ct) =>
        SendUpgradeResponseAsync(stream, wsKey, subProtocol: null, ct);

    /// <summary>
    /// Send HTTP 101 Switching Protocols response, selecting <paramref name="subProtocol"/> in
    /// <c>Sec-WebSocket-Protocol</c> when it is not <see langword="null"/>. The caller passes only a subprotocol the
    /// upgrade offered.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SHA1 is required by RFC 6455 WebSocket protocol")]
    internal static async Task SendUpgradeResponseAsync(Stream stream, string wsKey, string? subProtocol, CancellationToken ct)
    {
        var acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + WebSocketGuid)));

        var selected = subProtocol is null ? "" : $"Sec-WebSocket-Protocol: {subProtocol}\r\n";
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n{selected}\r\n";
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
        // A pending entry is claimed for its registrant instead, which withdraws it.
        foreach (var (session, entry) in _live)
        {
            if (entry.ClaimForStop())
                await session.DisposeAsync();
        }

        _live.Clear();
        _streams.Clear();

        WebSocketAudioServerLog.ServerStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        // Every disposal after the first is a no-op, whether or not the server was started.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (IsRunning) await StopAsync();
        _streamSubject.OnCompleted();
        _streamSubject.Dispose();
        _cts?.Dispose();
    }

    /// <summary>
    /// Where one connection's registration stands. It enters pending, and moves once: to admitted by
    /// its registrant, or to claimed by a stop.
    /// </summary>
    private sealed class RegistryEntry
    {
        private const int Pending = 0;
        private const int Admitted = 1;
        private const int ClaimedByStop = 2;

        private int _state = Pending;

        /// <summary>The registrant's move: true when the entry is now admitted, false when a stop claimed it first.</summary>
        public bool TryAdmit() =>
            Interlocked.CompareExchange(ref _state, Admitted, Pending) == Pending;

        /// <summary>
        /// The stop's move: claims a pending entry for its registrant to withdraw, and returns true only
        /// for an admitted entry, which the stop itself ends.
        /// </summary>
        public bool ClaimForStop() =>
            Interlocked.CompareExchange(ref _state, ClaimedByStop, Pending) == Admitted;
    }
}
