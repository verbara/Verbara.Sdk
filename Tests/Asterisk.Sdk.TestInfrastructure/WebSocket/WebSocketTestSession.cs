using System.Net.WebSockets;

namespace Asterisk.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// Per-connection context handed to the connection handler registered with
/// <see cref="WebSocketTestServer"/>. Exposes the bound server-side <see cref="System.Net.WebSockets.WebSocket"/>,
/// the captured request-target (path + query — needed by tests that assert query-string
/// parameters such as Speechmatics' <c>jwt=</c> or AssemblyAi's <c>sample_rate=</c>), and a
/// cancellation token that fires when the parent server is disposed.
/// </summary>
public sealed class WebSocketTestSession
{
    /// <summary>The accepted server-side WebSocket. Caller owns the protocol handlers.</summary>
    public System.Net.WebSockets.WebSocket WebSocket { get; }

    /// <summary>Raw HTTP request-target (e.g. <c>/v3/ws?sample_rate=16000</c>).</summary>
    public string? RequestUri { get; }

    /// <summary>Cancellation token tied to the parent server lifetime.</summary>
    public CancellationToken ServerCancellationToken { get; }

    internal WebSocketTestSession(
        System.Net.WebSockets.WebSocket webSocket,
        string? requestUri,
        CancellationToken serverCancellationToken)
    {
        WebSocket = webSocket;
        RequestUri = requestUri;
        ServerCancellationToken = serverCancellationToken;
    }
}
