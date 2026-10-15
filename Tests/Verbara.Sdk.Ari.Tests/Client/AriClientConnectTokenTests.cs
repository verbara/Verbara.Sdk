using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Client;

/// <summary>
/// What the token a caller hands <see cref="AriClient.ConnectAsync"/> reaches: the dial of that connect, and nothing
/// after it. A host that connects on its start or stopping token, or a request handler that connects on its request
/// token, cancels that token once the connect has returned; the connection it made goes on.
/// </summary>
/// <remarks>
/// <para>
/// The caller's own cancellation of a dial in progress is the other half of the rule, and is already pinned by
/// <c>AriClientStateTests.ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsBeforeTheBound</c> and
/// <c>ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt</c>.
/// </para>
/// <para>
/// No test here waits on a clock. The caller's token is cancelled with <see cref="CancellationTokenSource.Cancel()"/>,
/// which runs every callback registered on it before it returns, so whatever that cancellation does to the
/// connection has been done when the next line runs. Each test then waits for whichever of two signals comes first —
/// the one the requirement names, or the one the defect leaves — and asserts on which it was.
/// </para>
/// </remarks>
public sealed class AriClientConnectTokenTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private const string StasisStart = """{"type":"StasisStart","application":"test-app"}""";

    [Fact]
    public async Task ConnectAsync_ShouldKeepTheConnection_WhenTheCallerCancelsItsTokenAfterTheConnectReturned()
    {
        await using var peer = new EventPeer();
        var sut = new AriClient(ClientOptions(peer.Port, autoReconnect: false), NullLogger<AriClient>.Instance);
        var observer = new FirstEventObserver();
        using var subscription = sut.Subscribe(observer);
        using var caller = new CancellationTokenSource();

        try
        {
            await sut.ConnectAsync(caller.Token).AsTask().WaitAsync(WaitLimit);
            var connection = await peer.NextConnectionAsync();

            caller.Cancel();
            var sendFault = await Record.ExceptionAsync(() => connection.SendTextAsync(StasisStart));
            await Task.WhenAny(observer.FirstEvent, connection.Ended).WaitAsync(WaitLimit);

            using (new AssertionScope())
            {
                connection.Ended.IsCompleted.Should().BeFalse(
                    "the caller's token bounds the dial only, so cancelling it does not end the connection");
                sendFault.Should().BeNull("the far end's connection is still open");
                observer.FirstEvent.IsCompletedSuccessfully.Should().BeTrue(
                    "the event the far end sent after the cancellation is delivered to observers");
                sut.State.Should().Be(AriConnectionState.Connected);
                sut.EventLoop!.IsCompleted.Should().BeFalse("the events loop is still running");
            }
        }
        finally
        {
            await sut.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReconnectLoop_ShouldReconnect_WhenTheConnectionIsLostAfterTheCallerCancelledItsConnectToken()
    {
        await using var peer = new EventPeer();
        var sut = new AriClient(ClientOptions(peer.Port, autoReconnect: true), NullLogger<AriClient>.Instance);
        using var caller = new CancellationTokenSource();

        try
        {
            await sut.ConnectAsync(caller.Token).AsTask().WaitAsync(WaitLimit);
            var first = await peer.NextConnectionAsync();
            var loop = sut.EventLoop!;

            caller.Cancel();
            first.Drop();

            // The reconnect runs inside the events loop's task, so that task ends before a second dial only when no
            // reconnect was started.
            var second = peer.NextConnectionAsync();
            await Task.WhenAny(second, loop).WaitAsync(WaitLimit);

            using (new AssertionScope())
            {
                second.IsCompletedSuccessfully.Should().BeTrue(
                    "a connection lost after the caller cancelled its connect token is reconnected");
                loop.IsCompleted.Should().BeFalse("the reconnect loop runs on the client's own source");
            }

            if (second.IsCompletedSuccessfully)
            {
                (await WaitForConnectedAsync(sut)).Should().Be(
                    AriConnectionState.Connected, "the far end accepted the reconnect dial");
            }
        }
        finally
        {
            await sut.DisposeAsync();
        }
    }

    private static async Task<AriConnectionState> WaitForConnectedAsync(AriClient sut)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (sut.State != AriConnectionState.Connected && DateTime.UtcNow < deadline)
            await Task.Delay(10); // fence-allow: LOOP-DRIVER — AriClient exposes State but no state-change signal; bounded by WaitLimit
        return sut.State;
    }

    private static IOptions<AriClientOptions> ClientOptions(int port, bool autoReconnect) =>
        Microsoft.Extensions.Options.Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app",
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = TimeSpan.Zero,
            ReconnectMaxDelay = TimeSpan.Zero,
        });

    /// <summary>Completes with the first event the client delivers to its observers.</summary>
    private sealed class FirstEventObserver : IObserver<AriEvent>
    {
        private readonly TaskCompletionSource<AriEvent> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AriEvent> FirstEvent => _first.Task;

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(AriEvent value) => _first.TrySetResult(value);
    }

    /// <summary>
    /// The Asterisk side: answers every dial's upgrade and keeps each connection open, reading it until the client
    /// closes it or the connection ends, which <see cref="PeerConnection.Ended"/> reports.
    /// </summary>
    private sealed class EventPeer : IAsyncDisposable
    {
        private readonly TcpListener _server = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<PeerConnection> _connections = Channel.CreateUnbounded<PeerConnection>();
        private readonly List<PeerConnection> _all = [];
        private readonly Task _run;

        public EventPeer()
        {
            _server.Start();
            Port = ((IPEndPoint)_server.LocalEndpoint).Port;
            _run = Task.Run(RunAsync);
        }

        public int Port { get; }

        /// <summary>The next connection, once its upgrade has been answered.</summary>
        public Task<PeerConnection> NextConnectionAsync() =>
            _connections.Reader.ReadAsync().AsTask().WaitAsync(WaitLimit);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            PeerConnection[] all;
            lock (_all)
                all = [.. _all];
            foreach (var connection in all)
                await connection.DisposeAsync();
            _server.Stop();
            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            var ct = _stop.Token;
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _server.AcceptTcpClientAsync(ct);
                var stream = tcp.GetStream();
                var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, ct);
                await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, ct);
                var connection = new PeerConnection(
                    tcp, WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true }));
                lock (_all)
                    _all.Add(connection);
                _connections.Writer.TryWrite(connection);
            }
        }
    }

    private sealed class PeerConnection : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly WebSocket _ws;

        public PeerConnection(TcpClient tcp, WebSocket ws)
        {
            _tcp = tcp;
            _ws = ws;
            Ended = Task.Run(ReadUntilEndAsync);
        }

        /// <summary>Completes when the connection ended: a close from the client, or the connection dropped.</summary>
        public Task Ended { get; }

        public Task SendTextAsync(string json) =>
            _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .WaitAsync(WaitLimit);

        /// <summary>Drops the connection without a close frame, as a far end that went away does.</summary>
        public void Drop() => _tcp.Client.Close(0);

        public async ValueTask DisposeAsync()
        {
            _ws.Abort();
            _tcp.Dispose();
            await Ended.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            _ws.Dispose();
        }

        private async Task ReadUntilEndAsync()
        {
            var buffer = new byte[4096];
            try
            {
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                }
                while (result.MessageType != WebSocketMessageType.Close);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                // The connection dropped: an ending all the same, which is what this task reports.
            }
        }
    }
}
