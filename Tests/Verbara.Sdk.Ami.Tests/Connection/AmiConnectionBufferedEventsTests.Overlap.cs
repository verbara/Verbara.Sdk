using System.Collections.Concurrent;
using System.Globalization;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A loss with AutoReconnect redials while the lost session's buffer is still being delivered. The new session's events
/// are buffered from its login and delivered only after every event of the lost session; <c>Reconnected</c> follows the
/// lost session's last event; a second loss chains a third delivery behind the second; a caller's ending during the
/// overlap reports each released buffer on its own; and a session that overflows its buffer is still read at full speed.
/// </summary>
/// <remarks>
/// <para>
/// Every test holds the lost session's first dispatch on a gate and buffers events behind it, as the rest of this class
/// does, then closes the socket from the peer. The second socket's peer logs in as soon as the connection dials it. The
/// signal that the redial happened during the delivery is that login completing while the first dispatch is still held:
/// on a connection that awaits the whole delivery before it dials, it never completes while the gate is shut, and the
/// wait for it ends on <see cref="Bound"/>, which is the failure signal of these tests.
/// </para>
/// <para>
/// Order is read from what one <c>OnEvent</c> handler recorded: each PeerStatus's peer, in dispatch order, and the
/// position in that list at which each <c>Reconnected</c> handler ran.
/// </para>
/// </remarks>
public sealed partial class AmiConnectionBufferedEventsTests
{
    /// <summary>The lost session's undelivered events when the caller ends the connection during an overlap.</summary>
    private const int OverlapOldBuffered = 43;

    /// <summary>The redialled session's undelivered events when the caller ends the connection during an overlap.</summary>
    private const int OverlapNewBuffered = 31;

    private const string HeldPeer = "HOLD/old";

    // ── (a) the redial happens during the delivery, and order holds across the sessions ──────────────

    [Fact]
    public async Task ReconnectLoop_ShouldRedialWhileTheLostSessionsDispatchIsHeld_AndDeliverItsBufferBeforeAnyEventOfTheNewSession_WhenThePeerCloses()
    {
        await using var run = new OverlapRun();
        var first = await run.ConnectAsync();
        await run.HoldFirstDispatchAsync(first);
        await run.BufferAsync(first, "OLD", Buffered);

        var relogin = run.ServeNextLoginAsync();
        first.CloseFromPeer();
        var second = await ResultWithinBoundAsync(relogin);

        second.Should().NotBeNull(
            "the reconnect loop dials and logs in again while the lost session's dispatch is still held: the redial does " +
            $"not wait for the delivery of the {Buffered} buffered events");

        await run.BufferAsync(second!, "NEW", 3);
        run.Recorder.Open(HeldPeer);
        var delivered = await CompletesWithinBoundAsync(run.Recorder.CountReached(1 + Buffered + 3));
        var reconnected = await CompletesWithinBoundAsync(run.Recorder.ReconnectedReached(1));

        using (new AssertionScope())
        {
            delivered.Should().BeTrue("every event of both sessions is delivered once the held dispatch returns");
            reconnected.Should().BeTrue("the redialled session reports Reconnected");
            run.Recorder.Peers().Should().Equal([HeldPeer, .. Names("OLD", Buffered), .. Names("NEW", 3)],
                "the lost session's buffer is delivered, in order, before any event of the redialled session");
            run.Recorder.ReconnectedAt().Should().OnlyContain(at => at >= 1 + Buffered,
                "Reconnected is delivered after the lost session's last buffered event");
        }
    }

    // ── (b) Reconnected after the lost session's Hangup, which reaches the subscription it was buffered for ──

    [Fact]
    public async Task Reconnected_ShouldFollowTheLostSessionsHangup_AndTheOldSubscriptionShouldReceiveIt_WhenTheLoginLandsMidDrain()
    {
        await using var run = new OverlapRun();
        var old = new RecordingObserver();
        var subscription = run.Connection.Subscribe(old);
        var resubscribed = new RecordingObserver();
        IDisposable? replacement = null;
        var order = new ConcurrentQueue<string>();
        var resubscribedOnReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.OnHangup = () => order.Enqueue("hangup");
        run.Connection.Reconnected += () =>
        {
            order.Enqueue("reconnected");
            subscription.Dispose();
            replacement = run.Connection.Subscribe(resubscribed);
            resubscribedOnReconnect.TrySetResult();
        };
        var first = await run.ConnectAsync();
        await run.HoldFirstDispatchAsync(first);
        (await first.WriteEventAsync("Hangup", [new("Channel", "PJSIP/old-00000001"), new("Uniqueid", "1.1"), new("Cause", "16")]))
            .Should().BeTrue("the peer reports the lost session's Hangup");
        await run.FenceAsync(first);

        var relogin = run.ServeNextLoginAsync();
        first.CloseFromPeer();
        var second = await ResultWithinBoundAsync(relogin);

        second.Should().NotBeNull(
            "the reconnect loop logs in again while the lost session's Hangup is still buffered behind the held dispatch");

        // The hold opens only after the reconnect loop has logged that it logged in during the delivery (its connect has
        // returned; the loop writes that line on its own thread just before it waits), one event of the redialled session
        // is buffered behind a Ping answered on it, and every notification queued so far has been delivered. A loop that
        // queued Reconnected without waiting for the lost delivery queues it right after that line, with nothing to wait
        // for, so its handler runs first and disposes the subscription the Hangup was buffered for. No signal marks the
        // point where such a loop has queued it without a seam in the loop, so the Ping's round trip is a margin, not a
        // fence: in a process where that path is still cold the early Reconnected can land after the Hangup.
        (await CompletesWithinBoundAsync(run.Logger.Logged("[AMI] Logged in again while the lost session's events"))).Should()
            .BeTrue("the reconnect loop logs that it logged in while the lost session's events are still being delivered");
        await run.BufferAsync(second!, "NEW", 1);
        await run.Connection.PendingNotifications.WaitAsync(Bound);
        run.Recorder.Open(HeldPeer);
        var reconnected = await CompletesWithinBoundAsync(resubscribedOnReconnect.Task);
        replacement?.Dispose();

        using (new AssertionScope())
        {
            reconnected.Should().BeTrue("the redialled session reports Reconnected");
            order.Should().Equal(["hangup", "reconnected"],
                "Reconnected is delivered after the lost session's Hangup, which the subscription it was buffered for receives");
            resubscribed.Hangups.Should().Be(0, "a subscription made on Reconnected does not receive the lost session's Hangup");
        }
    }

    // ── (c) a caller's ending during the overlap reports each released buffer on its own ─────────────

    [Fact]
    public async Task DisposeAsync_ShouldReturnAfterTheHeldOldDispatch_AndReportEachReleasedBufferOnce_WhenTheCallerEndsDuringTheOverlap()
    {
        using var dropped = new CallerEndingDrops();
        await using var run = new OverlapRun();
        var first = await run.ConnectAsync();
        await run.HoldFirstDispatchAsync(first);
        await run.BufferAsync(first, "OLD", OverlapOldBuffered);

        var relogin = run.ServeNextLoginAsync();
        first.CloseFromPeer();
        var second = await ResultWithinBoundAsync(relogin);

        second.Should().NotBeNull(
            "the reconnect loop logs in again while the lost session's dispatch is held, so the caller's ending lands " +
            "during the overlap");

        await run.BufferAsync(second!, "NEW", OverlapNewBuffered);
        var asked = run.Recorder.Count;
        var end = run.Connection.DisposeAsync().AsTask();
        var endedBeforeTheHeldDispatch = end.IsCompleted;
        run.Recorder.Open(HeldPeer);
        var ended = await CompletesWithinBoundAsync(end);
        var warnings = run.Logger.Entries.Where(e => e.Format == DiscardedFormat).ToList();

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the caller's DisposeAsync returns once the held old dispatch has");
            endedBeforeTheHeldDispatch.Should().BeFalse("the caller's DisposeAsync waits for the held old dispatch");
            (run.Recorder.Count - asked).Should().Be(0, "no further event of either session is delivered after the ending");
            run.Connection.State.Should().Be(AmiConnectionState.Disconnected);
            warnings.Select(CountOf).Should().BeEquivalentTo(new long?[] { OverlapOldBuffered, OverlapNewBuffered },
                "each released buffer logs its own undelivered count once at Warning: the lost session's and the redialled one's");
            dropped.Measurements.Where(m => m is OverlapOldBuffered or OverlapNewBuffered).Should().BeEquivalentTo(
                new long[] { OverlapOldBuffered, OverlapNewBuffered },
                "each released buffer records its own caller_ending measurement");
        }
    }

    // ── (d) an ending from inside an old-session dispatch during the overlap completes ──────────────

    [Fact]
    public async Task DisposeAsync_ShouldComplete_WhenCalledFromInsideAnOldSessionDispatchDuringTheOverlap()
    {
        await using var run = new OverlapRun();
        var stateAfterEnding = new TaskCompletionSource<AmiConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Recorder.AfterHold = async () =>
        {
            await run.Connection.DisposeAsync();
            stateAfterEnding.TrySetResult(run.Connection.State);
        };
        var first = await run.ConnectAsync();
        await run.HoldFirstDispatchAsync(first);
        await run.BufferAsync(first, "OLD", HeldBufferedEvent);

        var relogin = run.ServeNextLoginAsync();
        first.CloseFromPeer();
        var second = await ResultWithinBoundAsync(relogin);

        second.Should().NotBeNull("the reconnect loop logs in again while the lost session's dispatch is held");

        run.Recorder.Open(HeldPeer);
        var state = await ValueWithinAsync(stateAfterEnding.Task, Bound);

        state.Should().Be(AmiConnectionState.Disconnected,
            "a DisposeAsync called from inside the lost session's dispatch during the overlap returns without waiting for " +
            "the dispatch it runs in, or for the redialled session's gated buffer behind it");
    }

    // ── (e) a second loss while the first drain is held chains a third delivery ─────────────────────

    [Fact]
    public async Task ReconnectLoop_ShouldDeliverEveryEventOfThreeSessionsOnceInOrder_AndReconnectedOnceForTheThird_WhenTheSecondSessionIsLostDuringTheFirstDrain()
    {
        await using var run = new OverlapRun();
        var first = await run.ConnectAsync();
        await run.HoldFirstDispatchAsync(first);
        await run.BufferAsync(first, "OLD", 5);

        var relogin = run.ServeNextLoginAsync();
        first.CloseFromPeer();
        var second = await ResultWithinBoundAsync(relogin);

        second.Should().NotBeNull("the reconnect loop logs in again while the lost session's dispatch is held");

        await run.BufferAsync(second!, "NEW", 4);
        var thirdLogin = run.ServeNextLoginAsync();
        second!.CloseFromPeer();
        var third = await ResultWithinBoundAsync(thirdLogin);
        third.Should().NotBeNull("the second session's loss is redialled too, while the first dispatch is still held");
        await run.BufferAsync(third!, "THIRD", 3);

        run.Recorder.Open(HeldPeer);
        var delivered = await CompletesWithinBoundAsync(run.Recorder.CountReached(1 + 5 + 4 + 3));
        var reconnected = await CompletesWithinBoundAsync(run.Recorder.ReconnectedReached(1));
        await run.Connection.PendingNotifications.WaitAsync(Bound);

        using (new AssertionScope())
        {
            delivered.Should().BeTrue("every event of the three sessions is delivered");
            run.Recorder.Peers().Should().Equal([HeldPeer, .. Names("OLD", 5), .. Names("NEW", 4), .. Names("THIRD", 3)],
                "every event of the three sessions is delivered once, in order");
            run.Recorder.MaxConcurrent.Should().Be(1, "never two dispatches at once");
            run.Logger.Entries.Where(e => e.Format == DiscardedFormat).Should().BeEmpty("a loss discards nothing");
            reconnected.Should().BeTrue("the surviving session reports Reconnected");
            run.Recorder.ReconnectedSessions().Should().Equal([3L],
                "Reconnected is delivered exactly once, for the third session, never for the dead second one");
        }
    }

    // ── the redialled session's dispatch never holds the thread that ends the lost delivery ──────────

    [Fact]
    public async Task Reconnected_ShouldBeRaisedBeforeTheNewSessionsHeldDispatchIsReleased_WhenTheLostDeliveryEndsWithTheNewBufferNonEmpty()
    {
        const string blockedPeer = "BLOCK/new";
        await using var run = new OverlapRun();
        using var block = new ManualResetEventSlim();
        var blockedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Holds the thread it is called on, not only the dispatch: a consumer that resumed inline on the thread that
        // completed the lost session's release would keep that thread, and whatever else continues on that release, here.
        run.Connection.OnEvent += evt =>
        {
            if (evt is PeerStatusEvent { Peer: blockedPeer })
            {
                blockedEntered.TrySetResult();
                block.Wait(Bound * 3);
            }

            return ValueTask.CompletedTask;
        };

        try
        {
            var first = await run.ConnectAsync();
            await run.HoldFirstDispatchAsync(first);
            await run.BufferAsync(first, "OLD", 3);

            var relogin = run.ServeNextLoginAsync();
            first.CloseFromPeer();
            var second = await ResultWithinBoundAsync(relogin);

            second.Should().NotBeNull("the reconnect loop logs in again while the lost session's dispatch is held");

            (await second!.WriteEventAsync("PeerStatus", [new("Peer", blockedPeer)])).Should().BeTrue();
            await run.BufferAsync(second, "NEW", 3);
            run.Recorder.Open(HeldPeer);
            var entered = await CompletesWithinBoundAsync(blockedEntered.Task);
            var reconnectedWhileHeld = await CompletesWithinBoundAsync(run.Recorder.ReconnectedReached(1));
            var stillHeld = !block.IsSet;
            block.Set();
            var delivered = await CompletesWithinBoundAsync(run.Recorder.CountReached(1 + 3 + 1 + 3));

            using (new AssertionScope())
            {
                entered.Should().BeTrue("once the lost session's buffer is delivered, the redialled session's first event is dispatched");
                reconnectedWhileHeld.Should().BeTrue(
                    "Reconnected is raised once the lost session's delivery is over, while the redialled session's first " +
                    "dispatch still holds its thread: that dispatch does not run on the thread that ended the delivery");
                stillHeld.Should().BeTrue("the redialled session's dispatch was still held when Reconnected was raised");
                delivered.Should().BeTrue("every event of both sessions is delivered once the held dispatch returns");
                run.Recorder.ReconnectedAt().Should().ContainSingle().Which.Should().BeInRange(1 + 3, 1 + 3 + 1,
                    "Reconnected follows the lost session's last event and precedes every event behind the held dispatch");
            }
        }
        finally
        {
            block.Set();
        }
    }

    // ── (g) premise pin: an overflowing session is still read at full speed ────────────────────────

    [Fact]
    public async Task ReaderLoop_ShouldReadAnOverflowingSessionAtFullSpeed_DroppingWhatTheBufferCannotHold_WhenItsDispatchIsHeld()
    {
        const int capacity = 2;
        const int written = 50;
        await using var run = new OverlapRun(eventPumpCapacity: capacity);
        var lost = 0;
        run.Connection.Lost += _ => Interlocked.Increment(ref lost);
        var first = await run.ConnectAsync();
        await run.HoldFirstDispatchAsync(first);

        var writes = 0;
        for (var i = 1; i < written; i++)
        {
            if (await CompletesWithinBoundAsync(first.WriteEventAsync("PeerStatus", [new("Peer", Name("OVER", i))])))
                writes++;
        }

        await run.FenceAsync(first);

        using (new AssertionScope())
        {
            writes.Should().Be(written - 1, "every write returns: the reader never waits for a handler");
            run.Connection.EventLossMark.DroppedBefore.Should().Be(written - 1 - capacity,
                $"of the {written} events, one is held in its dispatch and {capacity} fill the buffer; the rest are dropped");
            Volatile.Read(ref lost).Should().Be(0, "an overflow is no loss of the connection");
            first.IsConnected.Should().BeTrue("the socket stays open");
            run.Connection.State.Should().Be(AmiConnectionState.Connected);
        }
    }

    // ── Helpers of the overlap tests ─────────────────────────────────────────────────────────────────

    private static string Name(string prefix, int i) => prefix + "/" + i.ToString(CultureInfo.InvariantCulture);

    private static string[] Names(string prefix, int count) => [.. Enumerable.Range(0, count).Select(i => Name(prefix, i))];

    /// <summary>
    /// A connection with AutoReconnect and a 1 ms backoff, its peers, a recording <c>OnEvent</c> handler that holds the
    /// dispatch of <see cref="HeldPeer"/>, and a capturing logger. Disposing it opens the hold, ends the connection and
    /// stops the peers, each bounded.
    /// </summary>
    private sealed class OverlapRun : IAsyncDisposable
    {
        public OverlapRun(int? eventPumpCapacity = null)
        {
            Connection = new AmiConnection(Options.Create(new AmiConnectionOptions
            {
                Hostname = "localhost",
                Username = "admin",
                Password = "secret",
                EnableHeartbeat = false,
                AutoReconnect = true,
                ReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
                ReconnectMaxDelay = TimeSpan.FromMilliseconds(1),
                EventPumpCapacity = eventPumpCapacity ?? Verbara.Sdk.Ami.Internal.AsyncEventPump.DefaultCapacity,
            }), Factory, Logger);
            Recorder = new OverlapRecorder(Connection);
        }

        public PipedSocketFactory Factory { get; } = new();

        public CancellationTokenSource PeerCts { get; } = new(Bound * 6);

        public CapturingLogger<AmiConnection> Logger { get; } = new();

        public AmiConnection Connection { get; }

        public OverlapRecorder Recorder { get; }

        public async Task<PipedSocket> ConnectAsync()
        {
            var loggedIn = ServeNextLoginAsync();
            await Connection.ConnectAsync().AsTask().WaitAsync(Bound);
            return await loggedIn.WaitAsync(Bound);
        }

        /// <summary>The next socket the connection creates, once its peer has completed the login.</summary>
        public Task<PipedSocket> ServeNextLoginAsync() => Task.Run(async () =>
        {
            var peer = await Factory.NextAsync(PeerCts.Token);
            await peer.CompleteLoginAsync(PeerCts.Token);
            return peer;
        }, PeerCts.Token);

        public async Task HoldFirstDispatchAsync(PipedSocket socket)
        {
            (await socket.WriteEventAsync("PeerStatus", [new("Peer", HeldPeer)])).Should().BeTrue("the peer sends the first event");
            (await CompletesWithinBoundAsync(Recorder.Entered(HeldPeer))).Should().BeTrue(
                "the handler receives the first event and holds its dispatch");
        }

        /// <summary>The peer writes <paramref name="count"/> PeerStatus events named <c>prefix/i</c>, then answers a Ping.</summary>
        public async Task BufferAsync(PipedSocket socket, string prefix, int count)
        {
            for (var i = 0; i < count; i++)
                (await socket.WriteEventAsync("PeerStatus", [new("Peer", Name(prefix, i))])).Should().BeTrue();

            await FenceAsync(socket);
        }

        /// <summary>A Ping answered by the peer: every event written before it has been read once it returns.</summary>
        public async Task FenceAsync(PipedSocket socket)
        {
            await ConnectedAsync();
            var ping = Connection.SendActionAsync(new PingAction()).AsTask();
            var action = await socket.ReadActionAsync(PeerCts.Token).WaitAsync(Bound);
            PipedSocket.IsPing(action).Should().BeTrue("the connection sends the Ping");
            (await socket.RespondAsync("Success", PipedSocket.ActionIdOf(action!))).Should().BeTrue();
            (await CompletesWithinBoundAsync(ping)).Should().BeTrue("the Ping is answered");
        }

        /// <summary>
        /// Returns once the connection reads <see cref="AmiConnectionState.Connected"/>: a peer's login completes before
        /// the connection has started the session, and a Ping sent before then fails as not connected.
        /// </summary>
        private async Task ConnectedAsync()
        {
            if (Connection.State == AmiConnectionState.Connected)
                return;

            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChange(AmiConnectionStateChange change)
            {
                if (Connection.State == AmiConnectionState.Connected)
                    connected.TrySetResult();
            }

            Connection.StateChanged += OnChange;
            try
            {
                if (Connection.State == AmiConnectionState.Connected)
                    connected.TrySetResult();

                (await CompletesWithinBoundAsync(connected.Task)).Should().BeTrue("the connection starts the session the peer logged in");
            }
            finally
            {
                Connection.StateChanged -= OnChange;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Recorder.OpenAll();
            await CompletesWithinBoundAsync(Connection.DisposeAsync().AsTask());
            await PeerCts.CancelAsync();
            PeerCts.Dispose();
        }
    }

    /// <summary>
    /// Records, in dispatch order, the peer of every PeerStatus (and the type of any other event), the largest number of
    /// dispatches running at once, and for each <c>Reconnected</c> handler the position it ran at and the session it
    /// read. Holds the dispatch of any peer opened with <see cref="Entered"/> until <see cref="Open"/>.
    /// </summary>
    private sealed class OverlapRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<string?> _peers = [];
        private readonly List<int> _reconnectedAt = [];
        private readonly List<long> _reconnectedSessions = [];
        private readonly ConcurrentDictionary<string, HeldDispatch> _holds = new(StringComparer.Ordinal);
        private readonly List<(Func<bool> Reached, TaskCompletionSource Signal)> _waiters = [];
        private int _running;
        private int _maxConcurrent;

        public OverlapRecorder(AmiConnection connection)
        {
            connection.OnEvent += OnEventAsync;
            connection.Reconnected += () =>
            {
                lock (_gate)
                {
                    _reconnectedAt.Add(_peers.Count);
                    _reconnectedSessions.Add(connection.EventLossMark.Session);
                }

                Signal();
            };
        }

        /// <summary>Runs inside the held dispatch once its gate opens, before it returns.</summary>
        public Func<Task>? AfterHold { get; set; }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _peers.Count;
            }
        }

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public Task Entered(string peer) => Hold(peer).Entered;

        public void Open(string peer) => Hold(peer).Open();

        public void OpenAll()
        {
            foreach (var hold in _holds.Values)
                hold.Open();
        }

        public IReadOnlyList<string?> Peers()
        {
            lock (_gate)
                return [.. _peers];
        }

        public IReadOnlyList<int> ReconnectedAt()
        {
            lock (_gate)
                return [.. _reconnectedAt];
        }

        public IReadOnlyList<long> ReconnectedSessions()
        {
            lock (_gate)
                return [.. _reconnectedSessions];
        }

        public Task CountReached(int count) => When(() => _peers.Count >= count);

        public Task ReconnectedReached(int count) => When(() => _reconnectedAt.Count >= count);

        private HeldDispatch Hold(string peer) => _holds.GetOrAdd(peer, _ => new HeldDispatch());

        private Task When(Func<bool> reached)
        {
            lock (_gate)
            {
                if (reached())
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((reached, signal));
                return signal.Task;
            }
        }

        private void Signal()
        {
            List<TaskCompletionSource> fired = [];
            lock (_gate)
            {
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Reached())
                    {
                        fired.Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var signal in fired)
                signal.TrySetResult();
        }

        private async ValueTask OnEventAsync(ManagerEvent evt)
        {
            var running = Interlocked.Increment(ref _running);
            int max;
            while ((max = Volatile.Read(ref _maxConcurrent)) < running
                && Interlocked.CompareExchange(ref _maxConcurrent, running, max) != max)
            {
                // Another dispatch raised the maximum first; read it again.
            }

            try
            {
                var peer = (evt as PeerStatusEvent)?.Peer;
                lock (_gate)
                    _peers.Add(peer ?? evt.EventType);

                if (peer is not null && _holds.TryGetValue(peer, out var hold))
                {
                    hold.Enter();
                    await hold.Gate;
                    if (AfterHold is { } after)
                        await after();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                Signal();
            }
        }
    }

    /// <summary>An observer that counts the Hangups it receives and runs <see cref="OnHangup"/> for each.</summary>
    private sealed class RecordingObserver : IObserver<ManagerEvent>
    {
        private int _hangups;

        public Action? OnHangup { get; set; }

        public int Hangups => Volatile.Read(ref _hangups);

        public void OnNext(ManagerEvent value)
        {
            if (value is not HangupEvent)
                return;

            Interlocked.Increment(ref _hangups);
            OnHangup?.Invoke();
        }

        public void OnError(Exception error)
        {
            // The connection never reports one; nothing to observe.
        }

        public void OnCompleted()
        {
            // The connection never reports one; nothing to observe.
        }
    }
}
