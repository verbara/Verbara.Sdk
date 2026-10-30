using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>A recording peer. Protocol signals, rather than delays, advance its tests.</summary>
internal sealed class ClosingPeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Connection> _connections = Channel.CreateUnbounded<Connection>();
    private readonly List<Connection> _all = [];
    private readonly Task _accept;
    private int _dials;
    internal bool HoldCloseObservation { get; init; }
    internal bool HoldReconnectUpgrade { get; init; }
    internal bool AnswerClose { get; init; } = true;
    internal bool EventsBeforeAnswer { get; init; }
    internal int Dials => Volatile.Read(ref _dials);
    internal int Port { get; }
    internal ClosingPeer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }
    internal Task<Connection> NextAsync() => _connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
            var number = Interlocked.Increment(ref _dials);
            var stream = tcp.GetStream();
            var (key, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, _stop.Token);
            var connection = new Connection(this, tcp);
            lock (_all) _all.Add(connection);
            if (number > 1 && HoldReconnectUpgrade)
            {
                _connections.Writer.TryWrite(connection);
                continue;
            }
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, key!, _stop.Token);
            connection.Start();
            _connections.Writer.TryWrite(connection);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Connection[] all;
        lock (_all) all = [.. _all];
        foreach (var connection in all) await connection.DisposeAsync();
        _stop.Dispose();
    }
    internal sealed class Connection(ClosingPeer owner, TcpClient tcp) : IAsyncDisposable
    {
        private WebSocket? _ws;
        private Task _read = Task.CompletedTask;
        private int _closeInitiated;
        private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<bool> Ended => _ended.Task;
        private readonly TaskCompletionSource _closeObservationPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCloseObservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task CloseObservationPending => _closeObservationPending.Task;
        internal void ReleaseCloseObservation() => _releaseCloseObservation.TrySetResult();
        internal int CloseCount { get; private set; }
        internal WebSocketCloseStatus? CloseStatus { get; private set; }
        internal void Start()
        {
            _ws = WebSocket.CreateFromStream(tcp.GetStream(), new WebSocketCreationOptions { IsServer = true });
            _read = ReadAsync();
        }
        internal Task SendAsync(string value, bool end = true) => _ws!.SendAsync(Encoding.UTF8.GetBytes(value).AsMemory(), WebSocketMessageType.Text, end, owner._stop.Token).AsTask();
        internal Task CloseAsync()
        {
            Volatile.Write(ref _closeInitiated, 1);
            return _ws!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "peer close", owner._stop.Token);
        }
        internal void Drop() => tcp.Client.Close(0);
        private async Task ReadAsync()
        {
            try
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var result = await _ws!.ReceiveAsync(buffer.AsMemory(), owner._stop.Token);
                    if (result.MessageType != WebSocketMessageType.Close) continue;
                    // Pause only publication: ReceiveAsync has read the actual close bytes already.
                    // Hold only the answer to this side's own close. Not read from State == Closed:
                    // ManagedWebSocket publishes CloseSent only after its close write returns, so an
                    // answer that arrives first still reads CloseReceived and would skip the hold.
                    if (owner.HoldCloseObservation && Volatile.Read(ref _closeInitiated) == 1)
                    {
                        _closeObservationPending.TrySetResult();
                        await _releaseCloseObservation.Task.WaitAsync(owner._stop.Token);
                    }
                    CloseCount++;
                    CloseStatus = _ws.CloseStatus;
                    if (owner.EventsBeforeAnswer && _ws.State == WebSocketState.CloseReceived)
                    {
                        await SendAsync("{\"type\":\"StasisStart\",\"application\":\"test-app\"}");
                        await SendAsync("{\"type\":\"StasisStart\",\"application\":\"test-app\"}");
                    }
                    // A close this side started is not answered, even while its own write is still in flight.
                    if (owner.AnswerClose && _ws.State == WebSocketState.CloseReceived && Volatile.Read(ref _closeInitiated) == 0)
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "answer", owner._stop.Token);
                    if (_ws.State == WebSocketState.Closed) tcp.Dispose();
                    _ended.TrySetResult(true);
                    return;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                _ended.TrySetResult(false);
            }
        }
        public async ValueTask DisposeAsync()
        {
            _ws?.Abort();
            tcp.Dispose();
            await _read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _ws?.Dispose();
        }
    }
}
