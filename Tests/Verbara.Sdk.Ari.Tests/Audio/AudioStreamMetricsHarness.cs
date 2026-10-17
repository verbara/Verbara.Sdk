using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Tests.Shared.Sockets;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// One real call through an ARI audio server, for the meter witnesses: the server, the far end of its one
/// connection over an explicit IPv4 loopback, and the signals a test waits on instead of a delay.
/// </summary>
/// <remarks>
/// The server listens on port 0 and is never dialled: its accept seam hands it the one connection the
/// test accepted on its own 127.0.0.1:0 listener, wrapped so the test learns the moment the server's
/// handler releases it. That release is the handler's last step, after the session has been disposed,
/// and disposing a session waits for its loops, so once it is seen nothing more can be recorded for
/// that connection.
/// </remarks>
internal abstract class ServedAudioCall : IAsyncDisposable
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    public static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<ReleaseSignallingClient> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<IAudioStream> _announced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> _subscriptions = [];
    private int _accepts;

    protected ServedAudioCall()
    {
        Peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    /// <summary>The far end: what Asterisk would be.</summary>
    public Socket Peer { get; }

    /// <summary>Whether the server announced a stream for this connection.</summary>
    public bool WasAnnounced => _announced.Task.IsCompleted;

    /// <summary>The stream the server announced, as a consumer receives it.</summary>
    public Task<IAudioStream> Announced => _announced.Task;

    /// <summary>The server as the interface both transports implement.</summary>
    public abstract IAudioServer Server { get; }

    protected abstract void SetAcceptOverride(Func<CancellationToken, ValueTask<TcpClient>> accept);

    protected abstract ValueTask StartServerAsync();

    /// <summary>Stops the server, which disposes every session it holds.</summary>
    public abstract Task StopServerAsync();

    protected abstract ValueTask DisposeServerAsync();

    protected async Task StartCoreAsync()
    {
        _listener.Start();
        SetAcceptOverride(AcceptAsync);
        _subscriptions.Add(Server.OnStreamConnected.Subscribe(stream =>
        {
            // Subscribed inside the announcement, before the handler waits on the session, so the
            // completion of its notifications cannot be missed.
            _subscriptions.Add(stream.StateChanges.Subscribe(static _ => { }, () => _completed.TrySetResult()));
            _announced.TrySetResult(stream);
        }));

        await StartServerAsync();
        await Peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        await _accepted.Task.WaitAsync(SignalTimeout);
    }

    /// <summary>Waits until the server has announced the stream.</summary>
    public Task<IAudioStream> WaitAnnouncedAsync() => _announced.Task.WaitAsync(SignalTimeout);

    /// <summary>
    /// Waits until the server's handler has released the connection and, for an announced stream, until
    /// the stream has completed its state notifications. Both come after the session's loops have ended.
    /// </summary>
    public async Task WaitUntilReleasedAsync()
    {
        var client = await _accepted.Task.WaitAsync(SignalTimeout);
        await client.Released.WaitAsync(SignalTimeout);
        if (WasAnnounced)
            await _completed.Task.WaitAsync(SignalTimeout);
    }

    /// <summary>An abortive close: with a zero linger, closing sends RST instead of FIN.</summary>
    public void ResetPeer()
    {
        Peer.LingerState = new LingerOption(enable: true, seconds: 0);
        Peer.Dispose();
    }

    /// <summary>A TCP close without any protocol-level ending: FIN, then end of stream at the server.</summary>
    public void ClosePeerWithFin()
    {
        Peer.Shutdown(SocketShutdown.Both);
        Peer.Dispose();
    }

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions.ToArray())
            subscription.Dispose();
        await DisposeServerAsync();
        Peer.Dispose();
        _listener.Stop();
        if (_accepted.Task.IsCompletedSuccessfully)
            (await _accepted.Task).Dispose();
    }

    /// <summary>
    /// The server's accept seam: the first call hands over the one connection the peer made to this
    /// test's listener, and every later call parks until the server stops.
    /// </summary>
    private async ValueTask<TcpClient> AcceptAsync(CancellationToken token)
    {
        if (Interlocked.Increment(ref _accepts) > 1)
            return await AcceptedClients.ParkUntilCancelledAsync(token);

        var socket = await _listener.AcceptSocketAsync(token);
        var client = new ReleaseSignallingClient(socket);
        _accepted.TrySetResult(client);
        return client;
    }
}

/// <summary>A call through <see cref="AudioSocketServer"/>, fed bytes captured from a real Asterisk.</summary>
internal sealed class AudioSocketCall : ServedAudioCall
{
    private readonly AudioSocketServer _server;

    private AudioSocketCall(TimeSpan idleTimeout)
    {
        _server = new AudioSocketServer(
            new AudioServerOptions
            {
                AudioSocketPort = 0,
                ListenAddress = "127.0.0.1",
                DefaultFormat = "slin",
                IdleTimeout = idleTimeout,
            },
            new CapturingLogger<AudioSocketServer>());
    }

    public override IAudioServer Server => _server;

    public AudioSocketServer AudioSocketServer => _server;

    public static async Task<AudioSocketCall> StartAsync(TimeSpan? idleTimeout = null)
    {
        var call = new AudioSocketCall(idleTimeout ?? TimeSpan.FromSeconds(30));
        await call.StartCoreAsync();
        return call;
    }

    protected override void SetAcceptOverride(Func<CancellationToken, ValueTask<TcpClient>> accept) =>
        _server.AcceptOverride = accept;

    protected override ValueTask StartServerAsync() => _server.StartAsync();

    public override Task StopServerAsync() => _server.StopAsync().AsTask();

    protected override ValueTask DisposeServerAsync() => _server.DisposeAsync();

    /// <summary>Puts <paramref name="bytes"/> on the wire as they are.</summary>
    public async Task SendAsync(byte[] bytes) => await Peer.SendAsync(bytes);

    /// <summary>Sends the captured identification frame and waits until the server announces the stream.</summary>
    public async Task<IAudioStream> IdentifyAsync()
    {
        await SendAsync(AudioSocketWireCapture.IdentificationFrame());
        return await WaitAnnouncedAsync();
    }

    /// <summary>The first 8 kHz audio frame of the captured call: 320 bytes of payload.</summary>
    public static byte[] CapturedAudioFrame() => AudioSocketWireCapture.AudioFrame();

    /// <summary>
    /// An audio frame that declares a zero-length payload: the captured audio type byte with a length of
    /// <c>00 00</c>. No capture holds one; it is built from the captured header, not by the SDK's encoder.
    /// </summary>
    public static byte[] EmptyAudioFrame() => [AudioSocketWireCapture.AudioTypeByte, 0x00, 0x00];

    /// <summary>
    /// A hangup frame, <c>00 00 00</c>. Asterisk 22.9.0 sends none (see
    /// <see cref="AudioSocketWireCapture.HangupIsNotAFrameOnThisVersion"/>), so this is hand-built, as in
    /// the transport-failure tests, and never by the SDK's encoder.
    /// </summary>
    public static byte[] HangupFrame() => [0x00, 0x00, 0x00];

    /// <summary>An error frame, <c>FF 00 01 01</c>; no capture holds one, so it is hand-built.</summary>
    public static byte[] ErrorFrame() => [0xFF, 0x00, 0x01, 0x01];

    /// <summary>Reads exactly <paramref name="count"/> bytes the server sent to the far end.</summary>
    public async Task<byte[]> ReceiveAsync(int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await Peer.ReceiveAsync(buffer.AsMemory(read)).AsTask().WaitAsync(SignalTimeout);
            if (n == 0)
                break;
            read += n;
        }

        return buffer[..read];
    }
}

/// <summary>
/// A call through <see cref="WebSocketAudioServer"/>: a raw TCP client that sends the upgrade itself, so
/// the test owns the socket and can end it with a close frame, a FIN without one, or an RST.
/// </summary>
internal sealed class WebSocketCall : ServedAudioCall
{
    private const string ChannelId = "ws-metrics-witness";
    private readonly WebSocketAudioServer _server;
    private WebSocket? _client;
    private NetworkStream? _clientStream;

    private WebSocketCall()
    {
        _server = new WebSocketAudioServer(
            new AudioServerOptions
            {
                WebSocketPort = 0,
                ListenAddress = "127.0.0.1",
                DefaultFormat = "slin16",
                IdleTimeout = TimeSpan.FromSeconds(30),
            },
            new CapturingLogger<WebSocketAudioServer>());
    }

    public override IAudioServer Server => _server;

    public WebSocketAudioServer WebSocketServer => _server;

    /// <summary>The client end of the upgraded connection.</summary>
    public WebSocket Client => _client ?? throw new InvalidOperationException("Upgrade first.");

    public static async Task<WebSocketCall> StartAsync()
    {
        var call = new WebSocketCall();
        await call.StartCoreAsync();
        return call;
    }

    protected override void SetAcceptOverride(Func<CancellationToken, ValueTask<TcpClient>> accept) =>
        _server.AcceptOverride = accept;

    protected override ValueTask StartServerAsync() => _server.StartAsync();

    public override Task StopServerAsync() => _server.StopAsync().AsTask();

    protected override ValueTask DisposeServerAsync() => _server.DisposeAsync();

    /// <summary>Sends the upgrade, reads the 101, and waits until the server announces the stream.</summary>
    public async Task<IAudioStream> UpgradeAsync()
    {
        await SendUpgradeAsync();
        return await WaitAnnouncedAsync();
    }

    /// <summary>
    /// Sends the upgrade and reads the 101, without waiting for an announcement: the server starts the session
    /// right after the 101, whether or not it goes on to announce it.
    /// </summary>
    public async Task SendUpgradeAsync()
    {
        var request =
            $"GET /ws/{ChannelId} HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";
        await Peer.SendAsync(Encoding.ASCII.GetBytes(request));

        // Byte by byte up to the blank line, so nothing after the response is consumed here.
        var response = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(response))
        {
            var n = await Peer.ReceiveAsync(one.AsMemory()).AsTask().WaitAsync(SignalTimeout);
            if (n == 0)
                throw new InvalidOperationException("The server closed the connection during the upgrade.");
            response.Add(one[0]);
        }

        var status = Encoding.ASCII.GetString([.. response]);
        if (!status.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            throw new InvalidOperationException($"The upgrade was not accepted: {status}");

        _clientStream = new NetworkStream(Peer, ownsSocket: false);
        _client = WebSocket.CreateFromStream(
            _clientStream,
            new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
    }

    /// <summary>Sends one binary message.</summary>
    public async Task SendBinaryAsync(byte[] payload) =>
        await Client.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

    /// <summary>Sends the close frame, the orderly ending, without waiting for the server's.</summary>
    public async Task SendCloseFrameAsync() =>
        await Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

    /// <summary>Receives one whole binary message the server sent.</summary>
    public async Task<byte[]> ReceiveBinaryAsync()
    {
        var buffer = new byte[8192];
        var received = new List<byte>();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await Client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).AsTask().WaitAsync(SignalTimeout);
            received.AddRange(buffer.AsSpan(0, result.Count));
        }
        while (!result.EndOfMessage);

        return [.. received];
    }

    public override async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_clientStream is not null)
            await _clientStream.DisposeAsync();
        await base.DisposeAsync();
    }

    private static bool EndsWithBlankLine(List<byte> bytes) =>
        bytes.Count >= 4
        && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n';
}
