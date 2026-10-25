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
        finally { DisposeAll(peers); }
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
        finally { DisposeAll(peers); }
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
        finally { DisposeAll(peers); }
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
        var independentDisposalJoinedPublication = false;
        Task? competingDispose = null;
        using var subscription = connection.Events.Subscribe(_ => { }, () => Interlocked.Increment(ref completions));
        connection.CompletionStarting = () =>
        {
            Interlocked.Increment(ref hookCalls);
            // Do not inherit the publishing callback's execution context: this is another caller.
            using (ExecutionContext.SuppressFlow())
                competingDispose = Task.Run(() => connection.DisposeAsync().AsTask());
            var disposedBeforePublication = competingDispose.Wait(TimeSpan.FromSeconds(1));
            independentDisposalJoinedPublication = !disposedBeforePublication;
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
            independentDisposalJoinedPublication.Should().BeTrue("an independent caller waits until the target publication finishes");
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
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StopAsync_ShouldCompleteInsideTheObserver_WhenThePublishingConnectionIsStillTracked(bool disposeListener, bool stopAlreadyStarted)
    {
        var releasePump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig();
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var pumpParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackJoining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Listener.ReadEndedBeforeUntracking = () =>
        {
            pumpParked.TrySetResult();
            return releasePump.Task;
        };
        var stoppedInCallback = false;
        var trackedAtCallback = 0;
        Task? callbackStop = null;
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Listener.StopStarting = () =>
        {
            stopParked.TrySetResult();
            return releaseStop.Task;
        };
        using var subscription = rig.Last!.Events.Subscribe(_ => { }, () =>
        {
            trackedAtCallback = rig.Listener.ActiveConnectionCount;
            output.WriteLine($"dispose={disposeListener}; cached={stopAlreadyStarted}; publishing={rig.Last.IsPublishingOnCurrentContext}; context={SynchronizationContext.Current?.GetType().Name}");
            callbackStop = disposeListener ? rig.Listener.DisposeAsync().AsTask() : rig.Listener.StopAsync().AsTask();
            callbackJoining.TrySetResult();
            // The join was chosen while the snapshot was absent. Release on an independent
            // worker here so the test's own continuation cannot gate the callback's return.
            releaseStop.TrySetResult();
            stoppedInCallback = callbackStop.Wait(TimeSpan.FromSeconds(1));
            var transport = (TaskCompletionSource)typeof(AriOutboundListener).GetField("_stopTransportEnded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Listener)!;
            output.WriteLine($"callbackStop={callbackStop.Status}; transport={transport.Task.Status}; callbackReturned={stoppedInCallback}");
            callbackEnded.TrySetResult();
        });
        var read = ReadCloseAsync(peer);
        var disconnect = rig.Last.DisconnectAsync().AsTask();
        Task? firstStop = null;
        try
        {
            (await read.WaitAsync(Limit)).Should().BeTrue("the peer receives the application's actual close frame");
            if (stopAlreadyStarted)
            {
                firstStop = rig.Listener.StopAsync().AsTask();
                await stopParked.Task.WaitAsync(Limit);
            }
            await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "answer", CancellationToken.None);
            await pumpParked.Task.WaitAsync(Limit);
            await callbackJoining.Task.WaitAsync(Limit);
            releaseStop.TrySetResult();
            await callbackEnded.Task.WaitAsync(Limit);
            await disconnect.WaitAsync(Limit);
            await callbackStop!.WaitAsync(Limit);
            if (firstStop is not null) await firstStop.WaitAsync(Limit);
            using (new AssertionScope())
            {
                trackedAtCallback.Should().Be(1, "the publishing connection must still belong to the listener's stop snapshot");
                stoppedInCallback.Should().BeTrue("first stop, first disposal and pre-snapshot joins must not wait for their own publisher");
                rig.Listener.ActiveConnectionCount.Should().Be(0, "transport cleanup finishes before the callback's stop returns");
            }
        }
        finally
        {
            releaseStop.TrySetResult();
            releasePump.TrySetResult();
            peer.Abort();
            await disconnect.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
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
        try
        {
            using var peer = await rig.ConnectAsync();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_ShouldPreserveOuterPublicationOwnership_WhenCompletionCallbacksNest(bool stopOuterListener)
    {
        var releasePumps = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var outerRig = new Rig();
        await using var innerRig = new Rig();
        await outerRig.StartAsync();
        await innerRig.StartAsync();
        using var outerPeer = await outerRig.ConnectAsync();
        using var innerPeer = await innerRig.ConnectAsync();
        var outer = outerRig.Last!;
        var inner = innerRig.Last!;
        var parked = 0;
        var bothParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ParkPump()
        {
            if (Interlocked.Increment(ref parked) == 2) bothParked.TrySetResult();
            return releasePumps.Task;
        }
        outerRig.Listener.ReadEndedBeforeUntracking = ParkPump;
        innerRig.Listener.ReadEndedBeforeUntracking = ParkPump;
        var innerReturned = false;
        var outerReturned = false;
        var completions = 0;
        Task? nestedOuter = null;
        using var outerSubscription = outer.Events.Subscribe(_ => { }, () =>
        {
            Interlocked.Increment(ref completions);
            outerReturned = inner.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        });
        using var innerSubscription = inner.Events.Subscribe(_ => { }, () =>
        {
            Interlocked.Increment(ref completions);
            nestedOuter = stopOuterListener ? outerRig.Listener.StopAsync().AsTask() : outer.DisposeAsync().AsTask();
            innerReturned = nestedOuter.Wait(TimeSpan.FromSeconds(1));
        });
        var outerAnswer = ReadAndAnswerAsync(outerPeer);
        var innerAnswer = ReadAndAnswerAsync(innerPeer);
        var outerClose = outer.CloseForStopAsync(CancellationToken.None, CancellationToken.None);
        var innerClose = inner.CloseForStopAsync(CancellationToken.None, CancellationToken.None);
        try
        {
            (await outerAnswer.WaitAsync(Limit)).Should().BeTrue("the outer peer observes and answers its actual close");
            (await innerAnswer.WaitAsync(Limit)).Should().BeTrue("the inner peer observes and answers its actual close");
            await Task.WhenAll(outerClose, innerClose, bothParked.Task).WaitAsync(Limit);
            // A dedicated independent worker keeps this regression separate from synchronization-context startup.
            await Task.Factory.StartNew(() => outer.DisconnectAsync().AsTask().GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(Limit);
            await nestedOuter!.WaitAsync(Limit);
            using (new AssertionScope())
            {
                innerReturned.Should().BeTrue("nested completion must still recognize the outer publisher on the same synchronous stack");
                outerReturned.Should().BeTrue("the inner connection disposal returns before the outer observer returns");
                completions.Should().Be(2, "each real connection completes exactly once");
            }
        }
        finally
        {
            releasePumps.TrySetResult();
            outerPeer.Abort();
            innerPeer.Abort();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_ShouldProgressOffTheCallingContext_WhenACompletionObserverStopsSynchronously(bool disposeListener)
    {
        var releasePump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig();
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        rig.Listener.ReadEndedBeforeUntracking = () =>
        {
            parked.TrySetResult();
            return releasePump.Task;
        };
        var stoppedInCallback = false;
        var contextInCallback = false;
        Task? callbackStop = null;
        using var context = new SingleThreadTestContext();
        using var subscription = rig.Last!.Events.Subscribe(_ => { }, () =>
        {
            contextInCallback = ReferenceEquals(SynchronizationContext.Current, context);
            callbackStop = disposeListener ? rig.Listener.DisposeAsync().AsTask() : rig.Listener.StopAsync().AsTask();
            stoppedInCallback = callbackStop.Wait(TimeSpan.FromSeconds(1));
        });
        var peerClose = peer.CloseAsync(WebSocketCloseStatus.NormalClosure, "peer ends first", CancellationToken.None);
        try
        {
            await peerClose.WaitAsync(Limit);
            await parked.Task.WaitAsync(Limit);
            await Task.Factory.StartNew(() => context.Run(() => rig.Last.DisconnectAsync().AsTask(), () => callbackStop),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(Limit);
            using (new AssertionScope())
            {
                contextInCallback.Should().BeTrue("completion runs synchronously on the actual single-thread caller context");
                stoppedInCallback.Should().BeTrue("listener startup and disposal must progress while the completion callback blocks that context");
                rig.Listener.ActiveConnectionCount.Should().Be(0);
            }
        }
        finally
        {
            releasePump.TrySetResult();
            peer.Abort();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_ShouldFinishParallelCallbacks_WhenObserversEndEachOthersConnections(bool stopOtherListener)
    {
        var releasePumps = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstRig = new Rig();
        await using var secondRig = new Rig();
        await firstRig.StartAsync();
        await secondRig.StartAsync();
        using var firstPeer = await firstRig.ConnectAsync();
        using var secondPeer = await secondRig.ConnectAsync();
        var first = firstRig.Last!;
        var second = secondRig.Last!;
        var parked = 0;
        var bothParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ParkPump()
        {
            if (Interlocked.Increment(ref parked) == 2) bothParked.TrySetResult();
            return releasePumps.Task;
        }
        firstRig.Listener.ReadEndedBeforeUntracking = ParkPump;
        secondRig.Listener.ReadEndedBeforeUntracking = ParkPump;
        using var callbacks = new Barrier(2);
        var firstReturned = false;
        var secondReturned = false;
        var firstRendezvous = false;
        var secondRendezvous = false;
        var completions = 0;
        Task? firstJoin = null;
        Task? secondJoin = null;
        using var firstSubscription = first.Events.Subscribe(_ => { }, () =>
        {
            Interlocked.Increment(ref completions);
            firstRendezvous = callbacks.SignalAndWait(Limit);
            firstJoin = stopOtherListener ? secondRig.Listener.StopAsync().AsTask() : second.DisposeAsync().AsTask();
            firstReturned = firstJoin.Wait(TimeSpan.FromSeconds(1));
        });
        using var secondSubscription = second.Events.Subscribe(_ => { }, () =>
        {
            Interlocked.Increment(ref completions);
            secondRendezvous = callbacks.SignalAndWait(Limit);
            secondJoin = stopOtherListener ? firstRig.Listener.StopAsync().AsTask() : first.DisposeAsync().AsTask();
            secondReturned = secondJoin.Wait(TimeSpan.FromSeconds(1));
        });
        var firstAnswer = ReadAndAnswerAsync(firstPeer);
        var secondAnswer = ReadAndAnswerAsync(secondPeer);
        var firstClose = first.CloseForStopAsync(CancellationToken.None, CancellationToken.None);
        var secondClose = second.CloseForStopAsync(CancellationToken.None, CancellationToken.None);
        try
        {
            (await firstAnswer.WaitAsync(Limit)).Should().BeTrue("the first peer observes and answers its actual close");
            (await secondAnswer.WaitAsync(Limit)).Should().BeTrue("the second peer observes and answers its actual close");
            await Task.WhenAll(firstClose, secondClose, bothParked.Task).WaitAsync(Limit);
            var firstPublication = Task.Factory.StartNew(() => first.DisconnectAsync().AsTask().GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var secondPublication = Task.Factory.StartNew(() => second.DisconnectAsync().AsTask().GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.WhenAll(firstPublication, secondPublication).WaitAsync(Limit);
            await Task.WhenAll(firstJoin!, secondJoin!).WaitAsync(Limit);
            using (new AssertionScope())
            {
                firstRendezvous.Should().BeTrue("both completion publishers reach their callbacks on different workers");
                secondRendezvous.Should().BeTrue();
                firstReturned.Should().BeTrue("completion callbacks must not mutually join the other publisher");
                secondReturned.Should().BeTrue("completion callbacks must not mutually join the other publisher");
                completions.Should().Be(2, "both real connections publish exactly one completion");
            }
        }
        finally
        {
            releasePumps.TrySetResult();
            firstPeer.Abort();
            secondPeer.Abort();
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldDeferSubjectRelease_WhenAnotherCompletionCallbackDisposesThePublisher()
    {
        var releasePumps = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstRig = new Rig();
        await using var secondRig = new Rig();
        await firstRig.StartAsync();
        await secondRig.StartAsync();
        using var firstPeer = await firstRig.ConnectAsync();
        using var secondPeer = await secondRig.ConnectAsync();
        var first = firstRig.Last!;
        var second = secondRig.Last!;
        var parked = 0;
        var bothParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ParkPump()
        {
            if (Interlocked.Increment(ref parked) == 2) bothParked.TrySetResult();
            return releasePumps.Task;
        }
        firstRig.Listener.ReadEndedBeforeUntracking = ParkPump;
        secondRig.Listener.ReadEndedBeforeUntracking = ParkPump;
        using var releasePublication = new ManualResetEventSlim();
        var publicationClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.CompletionStarting = () =>
        {
            publicationClaimed.TrySetResult();
            if (!releasePublication.Wait(Limit)) throw new TimeoutException("The target publisher was not released.");
        };
        var secondCompletions = 0;
        var disposedInCallback = false;
        using var secondSubscription = second.Events.Subscribe(_ => { }, () => Interlocked.Increment(ref secondCompletions));
        using var firstSubscription = first.Events.Subscribe(_ => { }, () =>
            disposedInCallback = second.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)));
        var firstAnswer = ReadAndAnswerAsync(firstPeer);
        var secondAnswer = ReadAndAnswerAsync(secondPeer);
        var firstClose = first.CloseForStopAsync(CancellationToken.None, CancellationToken.None);
        var secondClose = second.CloseForStopAsync(CancellationToken.None, CancellationToken.None);
        Task? secondPublication = null;
        try
        {
            (await firstAnswer.WaitAsync(Limit)).Should().BeTrue();
            (await secondAnswer.WaitAsync(Limit)).Should().BeTrue();
            await Task.WhenAll(firstClose, secondClose, bothParked.Task).WaitAsync(Limit);
            secondPublication = Task.Factory.StartNew(() => second.DisconnectAsync().AsTask().GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await publicationClaimed.Task.WaitAsync(Limit);
            await Task.Factory.StartNew(() => first.DisconnectAsync().AsTask().GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(Limit);
            var subjectFault = Record.Exception(() => second.Events.Subscribe(_ => { }).Dispose());
            releasePublication.Set();
            await secondPublication.WaitAsync(Limit);
            using (new AssertionScope())
            {
                disposedInCallback.Should().BeTrue("a completion callback cannot join a different active publisher");
                subjectFault.Should().BeNull("release must wait for the target's publication, not the caller's publication identity");
                secondCompletions.Should().Be(1, "deferred release preserves the target's completion notification");
            }
        }
        finally
        {
            releasePublication.Set();
            releasePumps.TrySetResult();
            firstPeer.Abort();
            secondPeer.Abort();
            if (secondPublication is not null) await secondPublication.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_ShouldCompleteAcceptedObservers_WhenTheyEndAnActiveConnectionPublisher(bool stopListener)
    {
        var releasePump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig();
        await rig.StartAsync();
        using var peer = await rig.ConnectAsync();
        var connection = rig.Last!;
        rig.Listener.ReadEndedBeforeUntracking = () =>
        {
            pumpParked.TrySetResult();
            return releasePump.Task;
        };
        var stopEntries = 0;
        rig.Listener.StopStarting = () =>
        {
            Interlocked.Increment(ref stopEntries);
            return releaseStop.Task;
        };
        var connectionReturned = false;
        var acceptedReturned = false;
        var acceptedRole = false;
        var connectionThread = 0;
        var acceptedThread = 0;
        var connectionCompletions = 0;
        var acceptedCompletions = 0;
        Task? listenerDisposal = null;
        Task? acceptedEnding = null;
        Task? publication = null;
        using var acceptedSubscription = rig.Listener.OnConnectionAccepted.Subscribe(_ => { }, () =>
        {
            Interlocked.Increment(ref acceptedCompletions);
            acceptedThread = Environment.CurrentManagedThreadId;
            acceptedRole = AriOutboundConnection.IsInCompletionCallback;
            acceptedEnding = stopListener ? rig.Listener.StopAsync().AsTask() : connection.DisposeAsync().AsTask();
            acceptedReturned = acceptedEnding.Wait(TimeSpan.FromSeconds(1));
        });
        using var connectionSubscription = connection.Events.Subscribe(_ => { }, () =>
        {
            Interlocked.Increment(ref connectionCompletions);
            connectionThread = Environment.CurrentManagedThreadId;
            listenerDisposal = rig.Listener.DisposeAsync().AsTask();
            // Keep transport cleanup pending until DisposeAsync has selected its asynchronous join.
            // Its continuation must publish the accepted stream on another thread, outside this scope.
            releaseStop.TrySetResult();
            connectionReturned = listenerDisposal.Wait(TimeSpan.FromSeconds(1));
        });
        var answer = ReadAndAnswerAsync(peer);
        var transport = connection.CloseForStopAsync(CancellationToken.None, CancellationToken.None);
        try
        {
            (await answer.WaitAsync(Limit)).Should().BeTrue("the peer receives and answers the actual normal close");
            await Task.WhenAll(transport, pumpParked.Task).WaitAsync(Limit);
            publication = Task.Factory.StartNew(() => connection.DisconnectAsync().AsTask().GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await publication.WaitAsync(Limit);
            await listenerDisposal!.WaitAsync(Limit);
            await acceptedEnding!.WaitAsync(Limit);
            using (new AssertionScope())
            {
                stopEntries.Should().Be(1, "the stop-start fence forces listener disposal to suspend before completion publication");
                acceptedThread.Should().NotBe(connectionThread, "accepted completion executes after the asynchronous disposal continuation");
                acceptedRole.Should().BeTrue("accepted-stream completion must carry the callback role on its actual executing thread");
                acceptedReturned.Should().BeTrue("accepted completion must not join the connection publisher that awaits listener disposal");
                connectionReturned.Should().BeTrue("connection completion can synchronously dispose its listener without a completion cycle");
                connectionCompletions.Should().Be(1);
                acceptedCompletions.Should().Be(1);
            }
        }
        finally
        {
            releaseStop.TrySetResult();
            releasePump.TrySetResult();
            peer.Abort();
            if (publication is not null)
                await publication.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private sealed class SingleThreadTestContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _posts = new();
        public override void Post(SendOrPostCallback d, object? state) => _posts.Add(() => d(state));
        internal void Run(Func<Task> action, Func<Task?> joined)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                var operation = action();
                var deadline = Environment.TickCount64 + (long)Limit.TotalMilliseconds;
                while (!operation.IsCompleted || joined() is not { IsCompleted: true })
                {
                    if (Environment.TickCount64 >= deadline) throw new TimeoutException("The caller context did not drain its owned shutdown work.");
                    if (_posts.TryTake(out var next, 10)) next();
                }
                operation.GetAwaiter().GetResult();
                joined()!.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
        public void Dispose() => _posts.Dispose();
    }

    private static void DisposeAll(IEnumerable<ClientWebSocket> peers)
    {
        foreach (var peer in peers)
            peer.Dispose();
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
