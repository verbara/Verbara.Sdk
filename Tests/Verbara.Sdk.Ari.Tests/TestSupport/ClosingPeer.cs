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
    private int _order;
    internal bool HoldReconnectUpgrade { get; init; }
    internal bool AnswerClose { get; init; } = true;
    internal bool EventsBeforeAnswer { get; init; }
    internal int Dials => Volatile.Read(ref _dials);
    internal int SecondDialOrder { get; private set; }
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
            if (number == 2) SecondDialOrder = Interlocked.Increment(ref _order);
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
        private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<bool> Ended => _ended.Task;
        internal int CloseCount { get; private set; }
        internal int CloseOrder { get; private set; }
        internal WebSocketCloseStatus? CloseStatus { get; private set; }
        internal void Start()
        {
            _ws = WebSocket.CreateFromStream(tcp.GetStream(), new WebSocketCreationOptions { IsServer = true });
            _read = ReadAsync();
        }
        internal Task SendAsync(string value, bool end = true) => _ws!.SendAsync(Encoding.UTF8.GetBytes(value).AsMemory(), WebSocketMessageType.Text, end, owner._stop.Token).AsTask();
        internal Task CloseAsync() => _ws!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "peer close", owner._stop.Token);
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
                    CloseCount++;
                    CloseOrder = Interlocked.Increment(ref owner._order);
                    CloseStatus = _ws.CloseStatus;
                    if (owner.EventsBeforeAnswer && _ws.State == WebSocketState.CloseReceived)
                    {
                        await SendAsync("{\"type\":\"StasisStart\",\"application\":\"test-app\"}");
                        await SendAsync("{\"type\":\"StasisStart\",\"application\":\"test-app\"}");
                    }
                    if (owner.AnswerClose && _ws.State == WebSocketState.CloseReceived)
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
