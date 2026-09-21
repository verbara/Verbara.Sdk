using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Outbound;

internal static partial class AriOutboundListenerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[AriOutbound] Listener started: endpoint={Endpoint} path={Path}")]
    public static partial void ListenerStarted(ILogger logger, string endpoint, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AriOutbound] Listener stopped")]
    public static partial void ListenerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AriOutbound] Connection accepted: remote={RemoteEndpoint} app={Application}")]
    public static partial void ConnectionAccepted(ILogger logger, string remoteEndpoint, string application);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AriOutbound] Upgrade rejected: remote={RemoteEndpoint} reason={Reason}")]
    public static partial void UpgradeRejected(ILogger logger, string remoteEndpoint, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AriOutbound] Idle timeout — closing connection: remote={RemoteEndpoint} app={Application}")]
    public static partial void IdleTimeout(ILogger logger, string remoteEndpoint, string application);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AriOutbound] Connection closed: remote={RemoteEndpoint} app={Application}")]
    public static partial void ConnectionClosed(ILogger logger, string remoteEndpoint, string application);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AriOutbound] Connection error: remote={RemoteEndpoint}")]
    public static partial void ConnectionError(ILogger logger, Exception exception, string remoteEndpoint);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AriOutbound] Accept failed — the listener stays bound and accepts again after a backoff")]
    public static partial void AcceptLoopFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Default <see cref="IAriOutboundListener"/> implementation.
/// Opens a <see cref="TcpListener"/>, performs the RFC 6455 HTTP upgrade handshake,
/// and promotes validated connections to <see cref="AriOutboundConnection"/> instances.
/// </summary>
/// <remarks>
/// Event parsing reuses the existing <see cref="AriClient.ParseEvent"/> helper so that
/// this listener sees the same typed-event hierarchy (<c>AriJsonContext.Default</c>) as
/// the inbound <see cref="AriClient"/>. Both entry points MUST deserialize identically —
/// if the registry ever grows, <see cref="AriClient.ParseEvent"/> is still the single
/// source of truth.
/// </remarks>
public sealed class AriOutboundListener : IAriOutboundListener
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>The wait after the first of a run of failed accepts.</summary>
    internal static readonly TimeSpan InitialAcceptBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait between failed accepts, however long the run.</summary>
    internal static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(5);

    private readonly AriOutboundListenerOptions _options;
    private readonly ILogger<AriOutboundListener> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, TrackedConnection> _connections = new();
    private readonly Subject<AriOutboundConnection> _connectionSubject = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _running;

    public AriOutboundListener(
        IOptions<AriOutboundListenerOptions> options,
        ILogger<AriOutboundListener> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance whose accept backoff waits on <paramref name="timeProvider"/>, so a
    /// test can drive that wait with a fake clock instead of sitting out a real one.
    /// </summary>
    internal AriOutboundListener(
        IOptions<AriOutboundListenerOptions> options,
        ILogger<AriOutboundListener> logger,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Replaces the listener's accept when set. Settable by tests (via InternalsVisibleTo), so a test
    /// can make accepts fail on demand instead of exhausting file descriptors to get a failure.
    /// </summary>
    internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride { get; set; }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public int ActiveConnectionCount => _connections.Count;

    public IObservable<AriOutboundConnection> OnConnectionAccepted => _connectionSubject;

    public IEnumerable<AriOutboundConnection> ActiveConnections =>
        _connections.Values.Select(static c => c.Connection);

    public IEnumerable<AriOutboundConnection> GetByApplication(string applicationName) =>
        _connections.Values
            .Where(c => string.Equals(c.Connection.ApplicationName, applicationName, StringComparison.OrdinalIgnoreCase))
            .Select(static c => c.Connection);

    public ValueTask StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return ValueTask.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.Port);
        _listener.Start();

        var endpoint = _listener.LocalEndpoint.ToString() ?? $"{_options.ListenAddress}:{_options.Port}";
        AriOutboundListenerLog.ListenerStarted(_logger, endpoint, _options.Path);

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    /// <summary>Actual bound port — useful when tests pass Port=0 to get an ephemeral port.</summary>
    public int BoundPort =>
        _listener?.LocalEndpoint is IPEndPoint ep ? ep.Port : _options.Port;

    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        // Stopping a listener that is already down: the platform closed the socket under us, or a
        // concurrent DisposeAsync got there first. Either way the listener is not accepting, which is
        // the state this method exists to reach, so there is nothing to do and nothing to report.
        try { _listener?.Stop(); }
        catch (SocketException) { /* Best effort — the listener is already down */ }

        if (_cts is not null)
            await _cts.CancelAsync();

        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Snapshot then clear so concurrent callers don't race DisposeAsync.
        var snapshot = _connections.Values.ToArray();
        _connections.Clear();
        foreach (var tracked in snapshot)
        {
            try { await tracked.Connection.DisposeAsync(); }
            catch (WebSocketException) { /* Best effort */ }
            catch (ObjectDisposedException) { /* Best effort */ }
        }

        AriOutboundListenerLog.ListenerStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _connectionSubject.OnCompleted();
        _connectionSubject.Dispose();
        _cts?.Dispose();
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

                // CancellationToken.None on the hand-off, and deliberately so: this work item carries
                // the only reference to a connection already accepted, and a Task.Run token that
                // skipped it would leave that connection with no owner and nothing to close it. `ct`
                // travels with it as an argument instead. The sibling accept loop
                // (AudioSocketServer.AcceptLoopAsync) passes None for the same reason.
                _ = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);
                continue;
            }
            catch (OperationCanceledException)
            {
                // The stop path. `ct` is _cts.Token, cancelled by StopAsync (and so by DisposeAsync),
                // and the pending accept ended with it. Nothing is meant to be accepted after that, so
                // the loop ends rather than backing off.
                break;
            }
            catch (ObjectDisposedException)
            {
                // The Windows shape of that same stop: Stop() disposes the underlying socket under a
                // pending accept, and the accept surfaces the disposal. NOT reached on Linux, where
                // Stop() on a pending accept raises SocketException(OperationAborted) and the filtered
                // catch below takes it instead — this block is live on Windows, not dead code.
                break;
            }
            catch (SocketException) when (!IsRunning)
            {
                // The Linux shape of the stop: StopAsync clears _running BEFORE it calls Stop(), so an
                // accept aborted by that Stop() always arrives with IsRunning already false. IsRunning
                // is the discriminator and not `ct`, because StopAsync cancels _cts only after Stop()
                // returns — a filter on ct.IsCancellationRequested would race that ordering.
                break;
            }
            catch (SocketException ex)
            {
                // An accept that failed while the listener is still meant to be running: EMFILE/ENFILE,
                // a connection aborted in the backlog, ENOBUFS. Ending the loop here would leave
                // IsRunning reporting true over a socket still in LISTEN — the kernel would keep
                // completing handshakes nobody reads, so Asterisk's outbound connectors would hang
                // instead of being refused, and StartAsync could not restart it because _running is
                // already 1. So it is logged and the loop keeps accepting.
                AriOutboundListenerLog.AcceptLoopFailed(_logger, ex);
            }

            // A failure that persists fails every accept at once, and without a pause this loop would
            // spin and log without bound. The wait doubles with each consecutive failure up to the cap,
            // and a successful accept above starts it over.
            try
            {
                await Task.Delay(backoff, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                // Same stop path as above, caught while waiting out a backoff rather than while
                // accepting: _cts was cancelled by StopAsync. The loop ends.
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
        }
    }

    private ValueTask<TcpClient> AcceptAsync(CancellationToken ct) =>
        AcceptOverride?.Invoke(ct) ?? _listener!.AcceptTcpClientAsync(ct);

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        // The unconditional close, and now the only one: every ending below — the two rejected
        // handshakes, a swallowed stop, a logged failure, or the read pump returning — leaves this
        // block, so the connection is released before the method returns, exactly where the
        // hand-written `finally` released it. By then ReadPumpAsync's own `finally` has already
        // disposed the connection wrapping this client's stream, so the order is unchanged too.
        // This is ADR-0058 R4 — "exactly one place disposes it, and that place is the handler" —
        // now satisfied literally rather than approximately: one dispose site instead of three.
        using (client)
        {
            var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            NetworkStream? stream = null;
            try
            {
                stream = client.GetStream();

                var upgradeRequest = await ReadUpgradeRequestAsync(stream, ct);
                if (upgradeRequest is null)
                {
                    AriOutboundListenerLog.UpgradeRejected(_logger, remoteEndpoint, "malformed HTTP upgrade");
                    return;
                }

                var (rejectReason, appName) = Validate(upgradeRequest, _options);
                if (rejectReason is not null)
                {
                    AriOutboundListenerLog.UpgradeRejected(_logger, remoteEndpoint, rejectReason);
                    await SendErrorResponseAsync(stream, rejectReason, ct);
                    return;
                }

                await SendUpgradeResponseAsync(stream, upgradeRequest.WebSocketKey!, ct);

                var webSocket = WebSocket.CreateFromStream(
                    stream,
                    new WebSocketCreationOptions { IsServer = true });

                var subject = new Subject<AriEvent>();
                var connection = new AriOutboundConnection(appName!, remoteEndpoint, webSocket, subject);
                var tracked = new TrackedConnection(Guid.NewGuid(), connection, client, subject);
                _connections.TryAdd(tracked.Id, tracked);

                AriOutboundListenerLog.ConnectionAccepted(_logger, remoteEndpoint, appName!);
                _connectionSubject.OnNext(connection);

                await ReadPumpAsync(tracked, webSocket, subject, ct);
            }
            catch (OperationCanceledException)
            {
                // The stop token, `ct` — _cts.Token, cancelled by StopAsync and DisposeAsync. Every
                // await above is the handshake or the read pump under that token, so this is the
                // listener being stopped while this connection was being served. The `using (client)`
                // this method opens with still closes it.
                /* Best effort — the listener is stopping */
            }
            catch (WebSocketException ex)
            {
                AriOutboundListenerLog.ConnectionError(_logger, ex, remoteEndpoint);
            }
            catch (IOException ex)
            {
                AriOutboundListenerLog.ConnectionError(_logger, ex, remoteEndpoint);
            }
            catch (Exception ex)
            {
                AriOutboundListenerLog.ConnectionError(_logger, ex, remoteEndpoint);
            }
        }
    }

    private async Task ReadPumpAsync(
        TrackedConnection tracked,
        WebSocket webSocket,
        Subject<AriEvent> subject,
        CancellationToken ct)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(8192);
        var idleTimeout = _options.ConnectionIdleTimeout;
        var appName = tracked.Connection.ApplicationName;

        try
        {
            while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                bufferWriter.Clear();
                ValueWebSocketReceiveResult result;

                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (idleTimeout > TimeSpan.Zero)
                    idleCts.CancelAfter(idleTimeout);

                try
                {
                    do
                    {
                        var memory = bufferWriter.GetMemory(4096);
                        result = await webSocket.ReceiveAsync(memory, idleCts.Token);
                        bufferWriter.Advance(result.Count);
                    }
                    while (!result.EndOfMessage);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    AriOutboundListenerLog.IdleTimeout(_logger, tracked.Connection.RemoteEndpoint, appName);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Complete the close handshake so the client's CloseAsync returns cleanly.
                    try
                    {
                        if (webSocket.State == WebSocketState.CloseReceived)
                        {
                            await webSocket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "client closed",
                                CancellationToken.None);
                        }
                    }
                    catch (WebSocketException) { /* Best effort */ }
                    catch (ObjectDisposedException) { /* Best effort */ }
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text && bufferWriter.WrittenCount > 0)
                {
                    var json = Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
                    // TODO(v1.12.x): consider extracting ParseEvent into a dedicated helper
                    // (Verbara.Sdk.Ari.Internal.AriEventParser) once we have a third consumer.
                    var evt = AriClient.ParseEvent(json, _logger);
                    if (evt is not null)
                        subject.OnNext(evt);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The stop token, `ct` — _cts.Token, cancelled by StopAsync and DisposeAsync — and only
            // that. The idle timeout is the OTHER ending a cancellation can mean here, and it is
            // already taken by the filtered `when (!ct.IsCancellationRequested)` catch inside the loop
            // above, which logs it and breaks; anything reaching here is the listener stopping.
            /* Best effort — the listener is stopping */
        }
        catch (WebSocketException ex)
        {
            AriOutboundListenerLog.ConnectionError(_logger, ex, tracked.Connection.RemoteEndpoint);
        }
        finally
        {
            _connections.TryRemove(tracked.Id, out _);
            AriOutboundListenerLog.ConnectionClosed(_logger, tracked.Connection.RemoteEndpoint, appName);

            try { await tracked.Connection.DisposeAsync(); }
            catch (ObjectDisposedException) { /* Best effort */ }
        }
    }

    // ------------------------------------------------------------------ handshake

    internal sealed record UpgradeRequest(string Path, string? Query, string? WebSocketKey, string? Authorization);

    internal static async Task<UpgradeRequest?> ReadUpgradeRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (bytesRead == 0) return null;
            totalRead += bytesRead;

            if (Encoding.ASCII.GetString(buffer, 0, totalRead).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        var request = Encoding.ASCII.GetString(buffer, 0, totalRead);
        var lines = request.Split("\r\n");
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var fullPath = requestLine[1];
        var queryIdx = fullPath.IndexOf('?', StringComparison.Ordinal);
        var path = queryIdx >= 0 ? fullPath[..queryIdx] : fullPath;
        var query = queryIdx >= 0 ? fullPath[(queryIdx + 1)..] : null;

        string? wsKey = null;
        string? authorization = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                wsKey = line["Sec-WebSocket-Key:".Length..].Trim();
            else if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                authorization = line["Authorization:".Length..].Trim();
        }

        return new UpgradeRequest(path, query, wsKey, authorization);
    }

    internal static (string? rejectReason, string? application) Validate(
        UpgradeRequest request,
        AriOutboundListenerOptions options)
    {
        if (request.WebSocketKey is null)
            return ("missing Sec-WebSocket-Key", null);

        if (!string.Equals(request.Path, options.Path, StringComparison.Ordinal))
            return ($"path mismatch: expected={options.Path} actual={request.Path}", null);

        // The parentheses are load-bearing, not style: `&&` binds tighter than `||`, so dropping them
        // would parse this as `ExpectedUsername is not null || (ExpectedPassword is not null && !IsAuthorized(…))`
        // and invert the method. As written, `&&` short-circuits exactly where the outer `if` used to
        // guard — a listener with neither expectation configured still never consults the header.
        if ((options.ExpectedUsername is not null || options.ExpectedPassword is not null) &&
            !IsAuthorized(request.Authorization, options.ExpectedUsername, options.ExpectedPassword))
        {
            return ("basic auth mismatch", null);
        }

        var appName = ExtractApp(request.Query);
        if (appName is null)
            return ("missing app query parameter", null);

        if (options.AllowedApplications.Count > 0 &&
            !options.AllowedApplications.Contains(appName))
        {
            return ($"application '{appName}' not in allowed list", null);
        }

        return (null, appName);
    }

    private static bool IsAuthorized(string? authorizationHeader, string? expectedUser, string? expectedPass)
    {
        if (authorizationHeader is null) return false;
        const string prefix = "Basic ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var encoded = authorizationHeader[prefix.Length..].Trim();
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch (FormatException) { return false; }

        var sep = decoded.IndexOf(':', StringComparison.Ordinal);
        if (sep < 0) return false;

        var actualUser = decoded[..sep];
        var actualPass = decoded[(sep + 1)..];

        return string.Equals(actualUser, expectedUser ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(actualPass, expectedPass ?? string.Empty, StringComparison.Ordinal);
    }

    private static string? ExtractApp(string? query)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) continue;
            var key = pair[..eq];
            if (string.Equals(key, "app", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SHA1 is required by RFC 6455 WebSocket protocol")]
    internal static async Task SendUpgradeResponseAsync(Stream stream, string wsKey, CancellationToken ct)
    {
        var acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + WebSocketGuid)));

        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task SendErrorResponseAsync(Stream stream, string reason, CancellationToken ct)
    {
        // 400/401 without opening the WebSocket — mirrors how Asterisk itself rejects bad upgrades.
        var status = reason.Contains("auth", StringComparison.OrdinalIgnoreCase) ? "401 Unauthorized" : "400 Bad Request";
        var body = reason;
        var response = $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        await stream.FlushAsync(ct);
    }

    private sealed record TrackedConnection(
        Guid Id,
        AriOutboundConnection Connection,
        TcpClient Client,
        Subject<AriEvent> Subject);
}
