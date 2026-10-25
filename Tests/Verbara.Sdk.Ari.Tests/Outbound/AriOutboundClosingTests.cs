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
    public async Task StopAsync_ShouldReleaseTenMutePeersAtOneSecond_WhenTheSharedDeadlineExpires()
    {
        var clock = new FakeTimeProvider();
        await using var rig = new Rig(clock: clock);
        await rig.StartAsync();
        var peers = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < 10; i++) peers.Add(await rig.ConnectAsync());
            var reads = peers.Select(ReadCloseAsync).ToArray();
            var stop = rig.Listener.StopAsync().AsTask();
            var received = await Task.WhenAll(reads).WaitAsync(Limit);
            received.Should().OnlyContain(sawClose => sawClose, "all ten peers receive the close before the shared deadline");
            var timer = await clock.TimersCreated.ReadAsync().AsTask().WaitAsync(Limit);
            output.WriteLine($"peersObserved={received.Count(sawClose => sawClose)}; sharedDeadline={timer.DueTime}");
            clock.Advance(TimeSpan.FromMilliseconds(999));
            stop.IsCompleted.Should().BeFalse("mute peers keep the shared stop pending immediately before one second");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            rig.Listener.ActiveConnectionCount.Should().Be(0, "one shared second releases all ten mute peers");
            timer.DueTime.Should().Be(TimeSpan.FromSeconds(1), "the measured stop bound is one second, not the five-second default");
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }
    [Fact]
    public async Task StopAsync_ShouldUseItsSharedDeadline_WhenAnApplicationCloseIsAlreadyPending()
    {
        var clock = new FakeTimeProvider();
        await using var rig = new Rig(clock: clock);
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        rig.Last!.TimeProvider = new FakeTimeProvider();
        var read = ReadCloseAsync(peer);
        var ownClose = rig.Last.DisconnectAsync().AsTask();
        (await read.WaitAsync(Limit)).Should().BeTrue("the application's common close already sent its frame");
        var stop = rig.Listener.StopAsync().AsTask();
        try
        {
            await clock.TimersCreated.ReadAsync().AsTask().WaitAsync(Limit);
            clock.Advance(TimeSpan.FromSeconds(1));
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            ownClose.IsCompleted.Should().BeTrue("the stop joins and ends the same close attempt at its earlier shared deadline");
            rig.Listener.ActiveConnectionCount.Should().Be(0);
        }
        finally
        {
            peer.Abort();
            await Task.WhenAll(ownClose, stop).WaitAsync(Limit);
        }
    }
    [Fact]
    public async Task StopAsync_ShouldJoinThePendingClose_WhenStoppedAgainAndDisposed()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var read = ReadCloseAsync(peer);
        var first = rig.Listener.StopAsync().AsTask();
        (await read.WaitAsync(Limit)).Should().BeTrue("the first stop has started its close");
        var repeated = rig.Listener.StopAsync().AsTask();
        var dispose = rig.Listener.DisposeAsync().AsTask();
        try
        {
            repeated.IsCompleted.Should().BeFalse("a repeated stop joins the pending common close");
            dispose.IsCompleted.Should().BeFalse("listener disposal cannot release the source before that close ends");
            await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "answer", CancellationToken.None);
            await Task.WhenAll(first, repeated, dispose).WaitAsync(Limit);
        }
        finally
        {
            peer.Abort();
            await Task.WhenAll(first, repeated, dispose).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_ShouldPermitReentrantDisposal_WhenAnObserverCompletes(bool listenerStop)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var connection = rig.Last!;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? reentrantDispose = null;
        var disposedInCallback = false;
        var completions = 0;
        using var subscription = connection.Events.Subscribe(_ => { }, () =>
        {
            Interlocked.Increment(ref completions);
            reentrantDispose = connection.DisposeAsync().AsTask();
            // Bound the actual synchronous callback cycle so the unfixed regression can finish cleanup.
            disposedInCallback = reentrantDispose.Wait(TimeSpan.FromSeconds(1));
            completed.TrySetResult();
        });
        var answer = ReadAndAnswerAsync(peer);
        var close = listenerStop ? rig.Listener.StopAsync().AsTask() : connection.DisconnectAsync().AsTask();
        await completed.Task.WaitAsync(Limit);
        await close.WaitAsync(Limit);
        await reentrantDispose!.WaitAsync(Limit);
        using (new AssertionScope())
        {
            (await answer.WaitAsync(Limit)).Should().BeTrue("the peer answered the actual normal close before completion");
            disposedInCallback.Should().BeTrue("completion disposal must not wait for the task invoking that same callback");
            completions.Should().Be(1, "the common close notifies completion exactly once");
        }
    }
    [Fact]
    public async Task DisconnectAsync_ShouldNotifyExactlyOnce_WhenDisposalOverlapsCompletionPublication()
    {
        using var releasePump = new ManualResetEventSlim();
        await using var rig = new Rig(beforeClosed: () =>
        {
            if (!releasePump.Wait(Limit)) throw new TimeoutException("Pump release was not signalled.");
        });
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var connection = rig.Last!;
        var completions = 0;
        var hookCalls = 0;
        Task? competingDispose = null;
        using var subscription = connection.Events.Subscribe(_ => { }, () => Interlocked.Increment(ref completions));
        connection.CompletionStarting = () =>
        {
            Interlocked.Increment(ref hookCalls);
            // Do not inherit the publishing callback's execution context: this is another caller.
            using (ExecutionContext.SuppressFlow())
                competingDispose = Task.Run(() => connection.DisposeAsync().AsTask());
            var disposedBeforePublication = competingDispose.Wait(TimeSpan.FromSeconds(1));
            output.WriteLine($"hookCalls={hookCalls}; competingDispose={disposedBeforePublication}; completions={completions}");
        };
        var read = ReadCloseAsync(peer);
        var disconnect = connection.DisconnectAsync().AsTask();
        try
        {
            (await read.WaitAsync(Limit)).Should().BeTrue("the peer observes the actual connection close");
            await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "answer", CancellationToken.None);
            await disconnect.WaitAsync(Limit);
            await competingDispose!.WaitAsync(Limit);
            hookCalls.Should().Be(1, "the test reaches the gap after claiming completion and before publishing it");
            completions.Should().Be(1, "concurrent disposal must not suppress the common close's completion");
        }
        finally
        {
            releasePump.Set();
            peer.Abort();
            await disconnect.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_ShouldCompleteReentrantStop_WhenACompletionObserverStopsTheListener(bool listenerStop)
    {
        using var releasePump = new ManualResetEventSlim();
        await using var rig = new Rig(beforeClosed: () =>
        {
            if (!releasePump.Wait(Limit)) throw new TimeoutException("Pump release was not signalled.");
        });
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stoppedInCallback = false;
        using var subscription = rig.Last!.Events.Subscribe(_ => { }, () =>
        {
            stoppedInCallback = rig.Listener.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
            notified.TrySetResult();
        });
        var read = ReadCloseAsync(peer);
        var stop = listenerStop ? rig.Listener.StopAsync().AsTask() : rig.Last.DisconnectAsync().AsTask();
        try
        {
            (await read.WaitAsync(Limit)).Should().BeTrue("the peer observes the stop's actual close");
            await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "answer", CancellationToken.None);
            await notified.Task.WaitAsync(Limit);
            await stop.WaitAsync(Limit);
            stoppedInCallback.Should().BeTrue("completion must not synchronously wait for the stop awaiting that callback");
        }
        finally
        {
            releasePump.Set();
            peer.Abort();
            await stop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }
    [Fact]
    public async Task StopAsync_ShouldFinishCleanup_WhenACompletionObserverThrows()
    {
        using var releasePump = new ManualResetEventSlim();
        var rig = new Rig(beforeClosed: () =>
        {
            if (!releasePump.Wait(Limit)) throw new TimeoutException("Pump release was not signalled.");
        });
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var source = (CancellationTokenSource)typeof(AriOutboundListener).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Listener)!;
        using var subscription = rig.Last!.Events.Subscribe(_ => { }, () => throw new InvalidOperationException("completion observer failure"));
        var read = ReadCloseAsync(peer);
        var stop = rig.Listener.StopAsync().AsTask();
        try
        {
            (await read.WaitAsync(Limit)).Should().BeTrue("the peer observes the stop's actual close");
            await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "answer", CancellationToken.None);
            var fault = await Record.ExceptionAsync(() => stop.WaitAsync(Limit));
            using (new AssertionScope())
            {
                fault.Should().BeNull("a completion observer must not prevent listener shutdown cleanup");
                source.IsCancellationRequested.Should().BeTrue("stop must cancel pending handshakes even when an observer throws");
                rig.Listener.ActiveConnectionCount.Should().Be(0);
            }
        }
        finally
        {
            releasePump.Set();
            peer.Abort();
            await source.CancelAsync();
            _ = await Record.ExceptionAsync(() => rig.DisposeAsync().AsTask());
        }
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
    private sealed class BeforeClosedLogger(ILogger<AriOutboundListener> inner, Action beforeClosed) : ILogger<AriOutboundListener>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "ConnectionClosed") beforeClosed();
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Channel<AriOutboundConnection> _accepted = Channel.CreateUnbounded<AriOutboundConnection>();
        private readonly IDisposable _subscription;
        internal ClosingLogger<AriOutboundListener> Log { get; } = new();
        internal AriOutboundListener Listener { get; }
        internal AriOutboundConnection? Last { get; private set; }
        internal Rig(TimeSpan? idle = null, Action<AriOutboundConnection>? beforePump = null, TimeProvider? clock = null, Action? beforeClosed = null)
        {
            Listener = new AriOutboundListener(Options.Create(new AriOutboundListenerOptions
            {
                ListenAddress = "127.0.0.1", Port = 0, ConnectionIdleTimeout = idle ?? TimeSpan.FromMinutes(10)
            }), beforeClosed is null ? Log : new BeforeClosedLogger(Log, beforeClosed), clock ?? TimeProvider.System);
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
