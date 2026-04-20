using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Asterisk.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// In-process WebSocket test server based on <see cref="TcpListener"/> + manual HTTP/1.1
/// upgrade + <see cref="System.Net.WebSockets.WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <see cref="HttpListener"/>-based fakes whose <c>AcceptWebSocketAsync</c> + <c>ws.Abort()</c>
/// dispose path hangs on Linux test plumbing. The TcpListener path mirrors the production
/// <c>Asterisk.Sdk.Ari.Audio.WebSocketAudioServer</c> implementation, which is validated and
/// disposes cleanly in every scenario.
/// </para>
/// <para>
/// Each accepted connection invokes a caller-supplied per-connection handler that receives a
/// <see cref="WebSocketTestSession"/>. The session exposes the bound <see cref="System.Net.WebSockets.WebSocket"/>,
/// a cancellation token tied to the server lifetime, and the captured request URI (raw path + query).
/// </para>
/// </remarks>
public sealed class WebSocketTestServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<WebSocketTestSession, Task> _onConnection;
    private Task? _acceptLoop;

    /// <summary>The TCP port the server is listening on (loopback only).</summary>
    public int Port { get; }

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
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            var (wsKey, requestUri) = await ReadUpgradeRequestAsync(stream, _cts.Token).ConfigureAwait(false);
            if (wsKey is null)
            {
                client.Dispose();
                return;
            }

            await SendUpgradeResponseAsync(stream, wsKey, _cts.Token).ConfigureAwait(false);

            var ws = System.Net.WebSockets.WebSocket.CreateFromStream(
                stream,
                new WebSocketCreationOptions { IsServer = true });

            var session = new WebSocketTestSession(ws, requestUri, _cts.Token);
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
        }
    }

    /// <summary>
    /// Read the HTTP/1.1 upgrade request, returning the <c>Sec-WebSocket-Key</c> header value
    /// and the full request-target (path + query) for callers that need to assert on URL params.
    /// </summary>
    internal static async Task<(string? wsKey, string? requestUri)> ReadUpgradeRequestAsync(
        Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), ct).ConfigureAwait(false);
            if (bytesRead == 0) return (null, null);
            totalRead += bytesRead;

            if (Encoding.ASCII.GetString(buffer, 0, totalRead).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        var request = Encoding.ASCII.GetString(buffer, 0, totalRead);
        var lines = request.Split("\r\n");
        if (lines.Length == 0) return (null, null);

        // Request line: GET <request-target> HTTP/1.1 — preserve full target (path + query).
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3) return (null, null);
        var requestUri = requestLine[1];

        string? wsKey = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                wsKey = line["Sec-WebSocket-Key:".Length..].Trim();
                break;
            }
        }

        return (wsKey, requestUri);
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

    /// <summary>Stop the listener and cancel any in-flight handlers.</summary>
    public async ValueTask DisposeAsync()
    {
        try { _listener.Stop(); } catch { }
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { }
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
