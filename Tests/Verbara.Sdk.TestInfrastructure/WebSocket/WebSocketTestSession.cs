using System.Net.WebSockets;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// Per-connection context handed to the connection handler registered with
/// <see cref="WebSocketTestServer"/>. Exposes the bound server-side <see cref="System.Net.WebSockets.WebSocket"/>,
/// the captured request-target (path + query — needed by tests that assert query-string
/// parameters such as AssemblyAi's <c>sample_rate=</c>), the captured request headers, the stream
/// beneath the socket, and a cancellation token that fires when the parent server is disposed.
/// </summary>
public sealed class WebSocketTestSession
{
    /// <summary>The accepted server-side WebSocket. Caller owns the protocol handlers.</summary>
    public System.Net.WebSockets.WebSocket WebSocket { get; }

    /// <summary>Raw HTTP request-target (e.g. <c>/v3/ws?sample_rate=16000</c>).</summary>
    public string? RequestUri { get; }

    /// <summary>
    /// Every header of the upgrade request, keyed case-insensitively — the seam a fake needs to
    /// check the credential the client actually sent rather than assume one arrived.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// The upgraded connection's stream beneath <see cref="WebSocket"/>: the seam for a fake that must
    /// put a frame on the wire that the managed socket will not write.
    /// </summary>
    /// <remarks>
    /// The one such frame today is a close that carries no status code (RFC 6455 §5.5.1, an empty
    /// close payload). <c>CloseOutputAsync(WebSocketCloseStatus.Empty, …)</c> writes the reserved code
    /// 1005 into the payload instead, which RFC 6455 §7.4.1 forbids on the wire and a .NET peer rejects
    /// as a protocol error. Bytes written here bypass the socket's send lock and its state machine, and
    /// any <see cref="OutboundFrameGate"/>, so a caller writes only while it is the session's single
    /// writer and keeps its own record of what it sent.
    /// </remarks>
    public Stream Transport { get; }

    /// <summary>Cancellation token tied to the parent server lifetime.</summary>
    public CancellationToken ServerCancellationToken { get; }

    internal WebSocketTestSession(
        System.Net.WebSockets.WebSocket webSocket,
        string? requestUri,
        IReadOnlyDictionary<string, string> headers,
        Stream transport,
        CancellationToken serverCancellationToken)
    {
        WebSocket = webSocket;
        RequestUri = requestUri;
        Headers = headers;
        Transport = transport;
        ServerCancellationToken = serverCancellationToken;
    }
}
