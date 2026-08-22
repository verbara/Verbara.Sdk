using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// A TCP listener that accepts the connection, reads the client's HTTP upgrade request, and answers
/// it with <c>401 Unauthorized</c> instead of the <c>101</c> — the shape of a rejected API key.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="StalledHandshakeListener"/>: that one makes a cancel inside
/// <c>ClientWebSocket.ConnectAsync</c> orderable, this one makes a <em>genuine failure</em> there
/// orderable. Both matter to ADR-0053, which turns on telling the two apart — the same connect,
/// ended two different ways, must be counted two different ways. An unbound port would produce a
/// failure too, but only until something else on the machine binds it; here the rejection is
/// written by this process, so the outcome is not merely likely, it is authored.
/// </remarks>
internal sealed class RejectingHandshakeListener : IAsyncDisposable
{
    private const string Rejection =
        "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    private Task? _acceptLoop;

    /// <summary>The bound port. Valid after <see cref="Start"/>.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

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

            await stream.WriteAsync(Encoding.ASCII.GetBytes(Rejection), _cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (IOException) { /* the client gave up first */ }
        catch (SocketException) { /* same, one layer down */ }
    }

    public async ValueTask DisposeAsync()
    {
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
