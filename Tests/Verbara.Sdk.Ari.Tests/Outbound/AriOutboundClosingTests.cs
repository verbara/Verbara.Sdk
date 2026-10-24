using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reflection;
using Verbara.Sdk.Ari.Tests.TestSupport;
using System.Threading.Channels;
using Verbara.Sdk.Ari.Outbound;
using Verbara.Sdk.Ari.Tests.Client;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Outbound;

public sealed class AriOutboundClosingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_ShouldSendOneCloseToEveryConnection_WhenTokenIsLiveOrCancelled(bool cancelled)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var peers = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < 3; i++) peers.Add(await rig.ConnectAsync());
            var reads = peers.Select(ReadAndAnswerAsync).ToArray();
            using var stop = new CancellationTokenSource();
            if (cancelled) stop.Cancel();
            await rig.Listener.StopAsync(stop.Token).AsTask().WaitAsync(Limit);
            var results = await Task.WhenAll(reads).WaitAsync(Limit);
            results.Should().OnlyContain(sawClose => sawClose, "every peer must see the listener's close before TCP ends");
            rig.Listener.ActiveConnectionCount.Should().Be(0);
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }
    [Fact]
    public async Task StopAsync_ShouldReturnWithinOneBound_WhenTenPeersNeverAnswer()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var peers = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < 10; i++) peers.Add(await rig.ConnectAsync());
            await rig.Listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
            rig.Listener.ActiveConnectionCount.Should().Be(0);
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }
    [Fact]
    public async Task DisconnectAsync_ShouldLogClosedWithoutIdleTimeout_WhenItsOwnBoundExpires()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var connection = rig.Last!;
        var clock = new FakeTimeProvider();
        connection.TimeProvider = clock;
        var read = ReadCloseAsync(peer);
        var disconnect = connection.DisconnectAsync().AsTask();
        (await read.WaitAsync(Limit)).Should().BeTrue("the connection's own close went out");
        await clock.TimersCreated.ReadAsync().AsTask().WaitAsync(Limit);
        clock.Advance(TimeSpan.FromSeconds(5));
        await disconnect.WaitAsync(Limit);
        await rig.Log.Closed.Task.WaitAsync(Limit);
        using (new AssertionScope())
        {
            rig.Log.Entries.Should().NotContain(e => e.Id.Name == "IdleTimeout", "the close bound, not idle, ended the read");
            rig.Log.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
        }
    }
    [Fact]
    public async Task Pump_ShouldLogIdleTimeout_WhenItExpiresInTheMiddleOfAMessage()
    {
        using var beforePump = new ManualResetEventSlim();
        await using var rig = new Rig(TimeSpan.FromMilliseconds(100), _ =>
        {
            if (!beforePump.Wait(Limit)) throw new TimeoutException("The first fragment was not queued before the read pump started.");
        });
        using var hook = new WebSocketReceiveHook();
        hook.Arm(() => { });
        await hook.Watch(() => rig.StartAsync());
        ClientWebSocket? peer = null;
        try
        {
            peer = await rig.ConnectAsync();
            // The accepted observer still holds the pump: its idle source does not exist yet.
            await peer.SendAsync("{"u8.ToArray().AsMemory(), WebSocketMessageType.Text, false, CancellationToken.None);
            beforePump.Set();
            await hook.Fired.WaitAsync(Limit);
            await rig.Log.Closed.Task.WaitAsync(Limit);
            rig.Log.Entries.Should().Contain(e => e.Id.Name == "IdleTimeout");
            rig.Log.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
        }
        finally
        {
            beforePump.Set();
            peer?.Dispose();
        }
    }
    [Fact]
    public async Task Pump_ShouldNotLogError_WhenStopLandsInsideAPartialRead()
    {
        await using var rig = new Rig();
        using var hook = new WebSocketReceiveHook();
        await hook.Watch(() => rig.StartAsync());
        using var peer = await rig.ConnectAsync();
        Task? stop = null;
        hook.Arm(() =>
        {
            // Deliver the listener's own stop signal synchronously while this read still returns.
            // StopAsync's CancelAsync schedules callbacks; merely starting it can miss this wall.
            var source = (CancellationTokenSource)typeof(AriOutboundListener).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Listener)!;
            var socket = (WebSocket)typeof(AriOutboundConnection).GetField("_webSocket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Last)!;
            output.WriteLine($"partial callback before: socket={socket.State}; stopRequested={source.IsCancellationRequested}; sentCount=1; sentEndOfMessage=false");
            source.Cancel();
            output.WriteLine($"partial callback after: socket={socket.State}; stopRequested={source.IsCancellationRequested}");
            stop = rig.Listener.StopAsync().AsTask();
        });
        await peer.SendAsync("{"u8.ToArray().AsMemory(), WebSocketMessageType.Text, false, CancellationToken.None);
        await hook.Fired.WaitAsync(Limit);
        await stop!.WaitAsync(Limit);
        await rig.Log.Closed.Task.WaitAsync(Limit);
        output.WriteLine($"hookFired={hook.Fired.IsCompletedSuccessfully}; receivesStartedAfterwards={hook.ReceivesStartedAfterwards}");
        foreach (var entry in rig.Log.Entries) output.WriteLine($"{entry.Id.Name}: {entry.Level}; {entry.Exception}; {entry.Message}");
        rig.Log.Entries.Should().NotContain(e => e.Level == LogLevel.Error, "stop caused the aborted socket's next slice to fail");
    }
    [Fact]
    public async Task Pump_ShouldLogError_WhenTheTransportDiesWithoutAStop()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        peer.Abort();
        await rig.Log.Closed.Task.WaitAsync(Limit);
        rig.Log.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Id.Name == "ConnectionError", "a real unrequested transport failure is an Error");
        foreach (var entry in rig.Log.Entries) output.WriteLine($"{entry.Id.Name}: {entry.Level}; {entry.Exception}; {entry.Message}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_ShouldKeepReadingUntilTheDelayedAnswer_WhenEventsArrive(bool listenerStop)
    {
        await using var rig = new Rig();
        using var nextRead = new ReceiveStartSignal();
        await nextRead.Watch(() => rig.StartAsync());
        using var peer = await rig.ConnectAsync();
        await nextRead.Started.WaitAsync(Limit);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = rig.Last!.Events.Subscribe(_ => delivered.TrySetResult());
        nextRead.Arm();
        var peerRead = ReadCloseAsync(peer);
        var close = listenerStop ? rig.Listener.StopAsync().AsTask() : rig.Last.DisconnectAsync().AsTask();
        (await peerRead.WaitAsync(Limit)).Should().BeTrue("the common close starts with a frame");
        await peer.SendAsync("{\"type\":\"StasisStart\",\"application\":\"app\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        await delivered.Task.WaitAsync(Limit);
        await Task.WhenAny(nextRead.Started, rig.Log.Closed.Task).WaitAsync(Limit);
        rig.Log.Closed.Task.IsCompleted.Should().BeFalse("the pump must start the next read rather than dispose a close-sent socket");
        close.IsCompleted.Should().BeFalse("an event must not dispose the socket beneath a pending close");
        await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "delayed answer", CancellationToken.None);
        await close.WaitAsync(Limit);
        peer.State.Should().Be(WebSocketState.Closed);
        rig.Log.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }
    [Fact]
    public async Task DisconnectAsync_ShouldPreserveTheCloseAttempt_WhenAnAlreadyArmedIdleDeadlineExpires()
    {
        var idle = TimeSpan.FromMilliseconds(100);
        await using var rig = new Rig(idle);
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var read = ReadCloseAsync(peer);
        var close = rig.Last!.DisconnectAsync().AsTask();
        (await read.WaitAsync(Limit)).Should().BeTrue("the close starts before the existing idle deadline");
        // Pass the configured old idle deadline after observing the close frame.
        await Task.Delay(idle); // fence-allow: SETTLE — exercise the configured idle expiry while its close handshake is pending
        using (new AssertionScope())
        {
            close.IsCompleted.Should().BeFalse("the already-armed idle deadline cannot end the common close before its answer or close bound");
            rig.Log.Entries.Should().NotContain(e => e.Id.Name == "IdleTimeout", "the connection is now in its close handshake");
        }
        await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "answer after old idle deadline", CancellationToken.None);
        await close.WaitAsync(Limit);
    }

    private static async Task<bool> ReadAndAnswerAsync(ClientWebSocket peer)
    {
        if (!await ReadCloseAsync(peer)) return false;
        try { await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "answer", CancellationToken.None); }
        catch (WebSocketException) { return false; }
        return true;
    }
    private static async Task<bool> ReadCloseAsync(ClientWebSocket peer)
    {
        try
        {
            var result = await peer.ReceiveAsync(new byte[4096].AsMemory(), CancellationToken.None);
            return result.MessageType == WebSocketMessageType.Close && peer.CloseStatus == WebSocketCloseStatus.NormalClosure;
        }
        catch (WebSocketException) { return false; }
    }
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Channel<AriOutboundConnection> _accepted = Channel.CreateUnbounded<AriOutboundConnection>();
        private readonly IDisposable _subscription;
        internal ClosingLogger<AriOutboundListener> Log { get; } = new();
        internal AriOutboundListener Listener { get; }
        internal AriOutboundConnection? Last { get; private set; }
        internal Rig(TimeSpan? idle = null, Action<AriOutboundConnection>? beforePump = null)
        {
            Listener = new AriOutboundListener(Options.Create(new AriOutboundListenerOptions
            {
                ListenAddress = "127.0.0.1", Port = 0, ConnectionIdleTimeout = idle ?? TimeSpan.FromMinutes(10)
            }), Log);
            _subscription = Listener.OnConnectionAccepted.Subscribe(c =>
            {
                _accepted.Writer.TryWrite(c);
                beforePump?.Invoke(c);
            });
        }
        internal Task StartAsync() => Listener.StartAsync().AsTask();
        internal async Task<ClientWebSocket> ConnectAsync()
        {
            var peer = new ClientWebSocket();
            await peer.ConnectAsync(new Uri($"ws://127.0.0.1:{Listener.BoundPort}/ari/events?app=app"), CancellationToken.None);
            Last = await _accepted.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            return peer;
        }
        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            await Listener.DisposeAsync();
        }
    }
}
