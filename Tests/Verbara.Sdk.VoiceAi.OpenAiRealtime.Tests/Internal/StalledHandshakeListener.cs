using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// A TCP listener that accepts the connection, reads the client's HTTP upgrade request, and then
/// never answers it.
/// </summary>
/// <remarks>
/// This is the seam that makes a cancel inside <c>ClientWebSocket.ConnectAsync</c> orderable by
/// construction rather than by timing. <see cref="WebSocketTestServer"/> writes its <c>101</c>
/// before invoking the per-protocol handler, so nothing built on it can park a client mid-handshake;
/// a signal inside the fake's session handler fires on the wrong side of the window. Here the
/// handshake <em>cannot</em> complete, so once <see cref="RequestReceived"/> has fired, the only
/// thing that can end the client's connect is the token — the alternative outcome is not unlikely,
/// it is impossible. The socket is held on this listener's own token and released on disposal
/// (ADR-0045 rule 2).
/// </remarks>
internal sealed class StalledHandshakeListener : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _requestReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _acceptLoop;

    /// <summary>The bound port. Valid after <see cref="Start"/>.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Completes once the client's upgrade request has been read off the wire.</summary>
    public Task RequestReceived => _requestReceived.Task;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            using var socket = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            await using var stream = socket.GetStream();

            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read == 0) break;
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            _requestReceived.TrySetResult();

            // Hold the socket open until this listener is disposed. The 101 is never written, and
            // no clock is involved — an infinite Task.Delay would be a wall-clock barrier with no
            // honest fence-allow category.
            await _released.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (IOException) { /* the client gave up first */ }
        finally
        {
            _requestReceived.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _released.TrySetResult();
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _cts.Dispose();
    }
}
