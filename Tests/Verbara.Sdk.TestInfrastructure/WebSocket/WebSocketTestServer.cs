using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// In-process WebSocket test server based on <see cref="TcpListener"/> + manual HTTP/1.1
/// upgrade + <see cref="System.Net.WebSockets.WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <see cref="HttpListener"/>-based fakes whose <c>AcceptWebSocketAsync</c> + <c>ws.Abort()</c>
/// dispose path hangs on Linux test plumbing. The TcpListener path mirrors the production
/// <c>Verbara.Sdk.Ari.Audio.WebSocketAudioServer</c> implementation, which is validated and
/// disposes cleanly in every scenario.
/// </para>
/// <para>
/// Each accepted connection invokes a caller-supplied per-connection handler that receives a
/// <see cref="WebSocketTestSession"/>. The session exposes the bound <see cref="System.Net.WebSockets.WebSocket"/>,
/// a cancellation token tied to the server lifetime, the captured request URI (raw path + query) and
/// the captured request headers.
/// </para>
/// </remarks>
public sealed class WebSocketTestServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<WebSocketTestSession, Task> _onConnection;
    private readonly TaskCompletionSource _sessionCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _acceptLoop;
    private volatile System.Net.WebSockets.WebSocket? _currentSocket;

    /// <summary>The TCP port the server is listening on (loopback only).</summary>
    public int Port { get; }

    /// <summary>
    /// Parks outbound delivery after a chosen number of messages, so a test can cancel while this
    /// server still has frames to send. Set it before the client connects; leave it
    /// <see langword="null"/> — the default — and the session gets the raw socket, unchanged.
    /// </summary>
    /// <remarks>
    /// Opt-in on purpose: with no gate armed nothing about an accepted session differs from before
    /// this property existed, so no existing test can be perturbed by it. See
    /// <see cref="OutboundFrameGate"/> for why the hold belongs to the transport rather than to each
    /// fake's protocol handler.
    /// </remarks>
    public OutboundFrameGate? OutboundGate { get; set; }

    /// <summary>
    /// The server side's view of the current session's socket, or <see langword="null"/> before the
    /// first connection is accepted.
    /// </summary>
    /// <remarks>
    /// The condition a cancellation test needs to state and otherwise cannot: that the socket was
    /// still live when the token fired, rather than the stream having ended on the server's own
    /// close and the test crediting cancellation with someone else's work. Read through a volatile
    /// field because the accept loop writes it on a different thread from the one asserting on it.
    /// </remarks>
    public WebSocketState? SocketState => _currentSocket?.State;

    /// <summary>
    /// Completes when the first accepted session's handler returns — a join point for assertions
    /// about what the client did on its way out.
    /// </summary>
    /// <remarks>
    /// Without it, a test that asserts on the last thing a client sent is racing the session: the
    /// client's <c>StreamAsync</c> returns as soon as the server closes, which can be before the
    /// server has read what the client sent just before that. Awaiting this is what makes such an
    /// assertion deterministic rather than usually-right — see the half-close tests in the STT
    /// suites, whose first version passed against a client that half-closed for exactly that
    /// reason.
    /// </remarks>
    public Task SessionCompleted => _sessionCompleted.Task;

    /// <summary>
    /// Create a new server bound to a free loopback port. <paramref name="onConnection"/> is
    /// invoked for every accepted WebSocket — the per-protocol fake server provides this handler.
    /// </summary>
    public WebSocketTestServer(Func<WebSocketTestSession, Task> onConnection)
    {
        ArgumentNullException.ThrowIfNull(onConnection);
        _onConnection = onConnection;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Begin accepting incoming connections.</summary>
    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleConnectionAsync(client), _cts.Token);
            }
        }
        // DisposeAsync cancels _cts and stops the listener only after this loop has ended, so the loop
        // sees disposal only as that cancellation: the while check above, or this exception from the
        // cancelled accept. Anything else escapes to DisposeAsync, which awaits the loop with no catch.
        catch (OperationCanceledException) { /* DisposeAsync cancelled the pending accept */ }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            var (wsKey, requestUri, headers) = await ReadUpgradeRequestAsync(stream, _cts.Token).ConfigureAwait(false);
            if (wsKey is null)
            {
                client.Dispose();
                return;
            }

            await SendUpgradeResponseAsync(stream, wsKey, _cts.Token).ConfigureAwait(false);

            var raw = System.Net.WebSockets.WebSocket.CreateFromStream(
                stream,
                new WebSocketCreationOptions { IsServer = true });

            var ws = OutboundGate is { } gate
                ? new GatedWebSocket(raw, gate, _cts.Token)
                : raw;

            _currentSocket = ws;
            var session = new WebSocketTestSession(ws, requestUri, headers, _cts.Token);
            try
            {
                await _onConnection(session).ConfigureAwait(false);
            }
            finally
            {
                try { ws.Dispose(); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Swallow per-connection failures; the test asserts on observable side effects.
        }
        finally
        {
            try { client.Dispose(); } catch { }
            _sessionCompleted.TrySetResult();
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> means the session is ending — the peer closed, reset or
    /// dropped the connection, the socket was aborted or disposed, or the server or a ceiling
    /// cancelled the wait — rather than a defect in the fake or the test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fakes filter their socket catches on this instead of catching everything, so an exception
    /// that does not mean the session is over — a missing recording, an invalid close code — leaves
    /// the handler and reaches the per-connection boundary instead of passing for a peer that went
    /// away.
    /// </para>
    /// <para>
    /// The three types are the whole set these sockets produce, read from the runtime's
    /// <c>ManagedWebSocket</c> rather than assumed. A stream fault surfaces as
    /// <see cref="WebSocketException"/> with <see cref="WebSocketError.ConnectionClosedPrematurely"/>,
    /// and a call in the wrong state as <see cref="WebSocketException"/> with
    /// <see cref="WebSocketError.InvalidState"/>. Cancellation, and any fault after <c>Abort</c>,
    /// surfaces as <see cref="OperationCanceledException"/>. A call that races a dispose past the state
    /// check throws <see cref="ObjectDisposedException"/> — the substrate's dispose, or the socket's own
    /// when its close completes — which a background receive loop can meet in the middle of a session.
    /// <see cref="OutboundFrameGate"/> adds only <see cref="OperationCanceledException"/>. Receives
    /// queue behind one another rather than throwing, so a receive loop that overlaps a close
    /// handshake needs nothing more.
    /// </para>
    /// </remarks>
    public static bool IsSessionEnding(Exception exception) =>
        exception is WebSocketException or OperationCanceledException or ObjectDisposedException;

    /// <summary>
    /// Closes <paramref name="webSocket"/> with <paramref name="status"/> if it is open or has only
    /// received the peer's close, and returns quietly if the session ends first.
    /// </summary>
    /// <remarks>
    /// <c>CloseAsync</c> waits for the peer's answering close frame, so a client that disposes before
    /// sending one ends the handshake with a <see cref="WebSocketException"/>, and the state can
    /// change between the check and the call. Both mean the session is ending, which is all this
    /// swallows — see <see cref="IsSessionEnding"/>.
    /// </remarks>
    public static async Task CloseIfOpenAsync(
        System.Net.WebSockets.WebSocket webSocket, WebSocketCloseStatus status, string? statusDescription)
    {
        ArgumentNullException.ThrowIfNull(webSocket);

        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        try
        {
            await webSocket.CloseAsync(status, statusDescription, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSessionEnding(ex))
        {
            // Best effort: the peer may already have closed, reset or aborted the socket.
        }
    }

    /// <summary>
    /// Read the HTTP/1.1 upgrade request, returning the <c>Sec-WebSocket-Key</c> header value,
    /// the full request-target (path + query) for callers that need to assert on URL params, and
    /// every request header for callers that need to assert on the credential the client sent.
    /// </summary>
    /// <remarks>
    /// The headers were previously read for <c>Sec-WebSocket-Key</c> alone and then discarded, so a
    /// fake had no way to check whether the client authenticated — six of them accepted every
    /// connection regardless (<c>provider-wire-protocol-conformance</c> §2.3c). Capturing the whole
    /// set here is what lets a per-protocol fake assert on the credential rather than assume it.
    /// </remarks>
    internal static async Task<(string? wsKey, string? requestUri, IReadOnlyDictionary<string, string> headers)>
        ReadUpgradeRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), ct).ConfigureAwait(false);
            if (bytesRead == 0) return (null, null, EmptyHeaders);
            totalRead += bytesRead;

            if (Encoding.ASCII.GetString(buffer, 0, totalRead).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        var request = Encoding.ASCII.GetString(buffer, 0, totalRead);
        var lines = request.Split("\r\n");
        if (lines.Length == 0) return (null, null, EmptyHeaders);

        // Request line: GET <request-target> HTTP/1.1 — preserve full target (path + query).
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3) return (null, null, EmptyHeaders);
        var requestUri = requestLine[1];

        // Field names are case-insensitive (RFC 9110 §5.1); a repeated field is the comma-joined
        // list of its values (§5.2), which is what a server would see.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break; // end of the header block

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing)
                ? existing + ", " + value
                : value;
        }

        headers.TryGetValue("Sec-WebSocket-Key", out var wsKey);
        return (wsKey, requestUri, headers);
    }

    /// <summary>Send the HTTP 101 response with the RFC 6455 Sec-WebSocket-Accept hash.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is mandated by RFC 6455 for the Sec-WebSocket-Accept handshake.")]
    internal static async Task SendUpgradeResponseAsync(Stream stream, string wsKey, CancellationToken ct)
    {
        var acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + WebSocketGuid)));

        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cancel any in-flight handlers, wait for the accept loop to end, then stop the listener.</summary>
    public async ValueTask DisposeAsync()
    {
        // Cancel first, stop last. Stop must not overlap an accept: it clears the listener's active
        // flag and then nulls its socket, while an accept checks the flag and then reads the socket,
        // so the two can interleave into a NullReferenceException. The loop ends on the cancellation
        // alone, and Stop runs only once it has, when nothing can call accept again.
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            // No catch: anything that escapes the loop is unexpected and fails the fixture instead of
            // vanishing.
            if (_acceptLoop is not null)
                await _acceptLoop.ConfigureAwait(false);
        }
        finally
        {
            // Also after a faulted loop, so a failing fixture does not leave its port bound.
            try { _listener.Stop(); }
            catch (SocketException) { /* the one failure TcpListener.Stop documents; disposal carries on */ }
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
