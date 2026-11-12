using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The connection's two reads of the position its delivery has reached: <see cref="AmiConnection.DeliveredLossMark"/>,
/// the mark of the event being dispatched inside a dispatch and, anywhere else, of the event whose dispatch started
/// last; and <see cref="AmiConnection.AppliedLossMark"/>, the mark of the event whose observers all returned last. Read
/// outside a dispatch, neither counts a drop the reader refused while the event it names waited or ran, which
/// <see cref="AmiConnection.EventLossMark"/> does.
/// </summary>
/// <remarks>
/// <para>
/// Every wait on a blocked observer or handler is gate-sequenced: the observer (or handler) signals that it has entered
/// before it blocks on the test's gate, the test reads only after that signal and releases the gate only after its reads.
/// The pump awaits each dispatch before it starts the next, so nothing races the reads. A Ping the peer answers is the
/// fence: once it returns, the reader has handed every earlier event to the buffer, or refused it. No wall clock is read
/// except <see cref="Bound"/>, a hang bound every wait ends long before.
/// </para>
/// <para>
/// The class runs in <see cref="AmiEventsDroppedMetricGroup"/> because its drops move the process-wide
/// <c>ami.events.dropped</c> counter another class of that group counts.
/// </para>
/// </remarks>
[Collection(AmiEventsDroppedMetricGroup.Name)]
public sealed class AmiConnectionDeliveredAndAppliedMarkTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DeliveredLossMark_ShouldReadTheFrameMark_WhenReadInsideADispatch()
    {
        await using var rig = await Rig.StartAsync(capacity: 2);
        var held = rig.HoldInHandler("HOLD/frame");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "HOLD/frame")]);
        await held.Entered.WaitAsync(Bound);
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "PROBE")]);
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "FILL/0")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/lost-1"), new("Uniqueid", "7.1")]);
        await rig.FenceAsync();
        var session = rig.Connection.EventLossMark.Session;
        var readerSide = rig.Connection.EventLossMark;
        held.Release();
        var inside = await rig.ProbeSeen.WaitAsync(Bound);

        using (new AssertionScope())
        {
            readerSide.Should().Be(new AmiEventLossMark(session, 1), "premise: the full buffer refused the Newstate");
            inside.EventLoss.Should().Be(new AmiEventLossMark(session, 0),
                "premise: the probe arrived before the drop, and inside its dispatch the frame is its arrival-time mark");
            inside.Delivered.Should().Be(inside.EventLoss,
                "inside a dispatch the delivered mark is the dispatch frame's, never the reader's count now");
        }
    }

    [Fact]
    public async Task DeliveredLossMark_ShouldReadTheLastDeliveredEvent_WhenADropIsStillAhead()
    {
        // A drop still ahead of delivery, on a capacity-1 buffer: H is being dispatched, B is buffered behind it, X is refused.
        await using var rig = await Rig.StartAsync(capacity: 1);
        var held = rig.HoldInHandler("HOLD/ahead");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "HOLD/ahead")]);
        await held.Entered.WaitAsync(Bound);
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "BUFFERED/B")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/lost-x"), new("Uniqueid", "7.2")]);
        await rig.FenceAsync();
        var session = rig.Connection.EventLossMark.Session;
        var readerSide = rig.Connection.EventLossMark;
        var delivered = rig.Connection.DeliveredLossMark;
        held.Release();

        using (new AssertionScope())
        {
            readerSide.Should().Be(new AmiEventLossMark(session, 1), "premise: the reader refused X behind the buffered B");
            delivered.Should().Be(new AmiEventLossMark(session, 0),
                "the last event whose dispatch started is H, which arrived before the drop; X is still ahead of delivery");
        }
    }

    [Fact]
    public async Task DeliveredLossMark_ShouldReadTheLastDeliveredEvent_WhenTheConnectionIsDown()
    {
        await using var rig = await Rig.StartAsync(capacity: 1);
        var held = rig.HoldInHandler("HOLD/down");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "HOLD/down")]);
        await held.Entered.WaitAsync(Bound);
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "BUFFERED/B")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/lost-d"), new("Uniqueid", "7.3")]);
        await rig.FenceAsync();
        held.Release();
        // The pump holds one event: the probe is sent only once the buffered B has been taken out and handled, or the
        // full buffer refuses the probe too.
        await rig.HandledAsync("BUFFERED/B");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "PROBE")]);
        var probe = await rig.ProbeHandled.WaitAsync(Bound);

        // A second drop the delivery never reaches: Q is buffered behind H2 and X2 is refused; once H2 is released Q is
        // dispatched with the probe's count, and X2 stays counted only on the reader side.
        var second = rig.HoldInHandler("HOLD/down/2");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "HOLD/down/2")]);
        await second.Entered.WaitAsync(Bound);
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "QUEUED/down")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/lost-d2"), new("Uniqueid", "7.4")]);
        await rig.FenceAsync();
        var readerSide = rig.Connection.EventLossMark;
        second.Release();
        var lastDelivered = await rig.HandledAsync("QUEUED/down");
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connection.Lost += _ => lost.TrySetResult();

        rig.Peer.CloseFromPeer();
        await lost.Task.WaitAsync(Bound);
        var delivered = rig.Connection.DeliveredLossMark;

        using (new AssertionScope())
        {
            probe.Should().Be(new AmiEventLossMark(probe.Session, 1), "premise: the probe arrived after the drop");
            readerSide.Should().Be(new AmiEventLossMark(probe.Session, 2), "premise: the reader refused X2 as well");
            lastDelivered.Should().Be(probe, "premise: Q arrived before X2 was refused");
            rig.Connection.State.Should().NotBe(AmiConnectionState.Connected, "premise: the peer closed the session");
            delivered.Should().Be(lastDelivered,
                "with the connection down the delivered mark is the last delivered event's, not a session that has not begun");
        }
    }

    [Fact]
    public async Task DeliveredLossMark_ShouldReadTheSessionWithZeroDrops_WhenNothingWasEverDispatched()
    {
        var factory = new PipedSocketFactory();
        await using var connection = new AmiConnection(Options.Create(Rig.OptionsWith(capacity: 1)), factory,
            NullLogger<AmiConnection>.Instance);
        var beforeConnect = (connection.DeliveredLossMark, connection.AppliedLossMark);

        using var peerCts = new CancellationTokenSource(Bound * 2);
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await loggedIn.WaitAsync(Bound);
        var afterConnect = (connection.DeliveredLossMark, connection.AppliedLossMark);

        using (new AssertionScope())
        {
            beforeConnect.Should().Be((new AmiEventLossMark(0, 0), new AmiEventLossMark(0, 0)),
                "before the first session there is no session and nothing was dropped");
            afterConnect.Should().Be((new AmiEventLossMark(1, 0), new AmiEventLossMark(1, 0)),
                "a session that has dispatched nothing reads its own ordinal with no drops on both sides");
        }
    }

    [Fact]
    public async Task Incarnation_ShouldDiffer_WhenTwoConnectionsAreBuilt()
    {
        await using var first = new AmiConnection(Options.Create(Rig.OptionsWith(capacity: 1)), new PipedSocketFactory(),
            NullLogger<AmiConnection>.Instance);
        await using var second = new AmiConnection(Options.Create(Rig.OptionsWith(capacity: 1)), new PipedSocketFactory(),
            NullLogger<AmiConnection>.Instance);

        using (new AssertionScope())
        {
            first.Incarnation.Should().NotBe(Guid.Empty, "an identity drawn at construction is never the empty one");
            first.Incarnation.Should().NotBe(second.Incarnation, "each connection instance draws its own identity");
            first.Incarnation.Should().Be(first.Incarnation, "the identity is drawn once and read the same every time");
        }
    }

    [Fact]
    public async Task AppliedLossMark_ShouldLagTheDeliveredMark_WhileAnObserverIsStillRunning()
    {
        await using var rig = await Rig.StartAsync(capacity: 1);
        var before = await rig.DeliverThenDropAsync("lag");
        var after = before with { DroppedBefore = before.DroppedBefore + 1 };
        var parked = rig.ParkInObserver("OBSERVED/lag");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "OBSERVED/lag")]);
        parked.Entered.Wait(Bound).Should().BeTrue("premise: the observer of the event after the drop has entered");
        var delivered = rig.Connection.DeliveredLossMark;
        var applied = rig.Connection.AppliedLossMark;
        parked.Release();

        using (new AssertionScope())
        {
            delivered.Should().Be(after, "the running event, the first after the drop, has started its dispatch");
            applied.Should().Be(before, "its observer has not returned, so the last applied event is the previous one");
        }
    }

    [Fact]
    public async Task AppliedLossMark_ShouldEqualTheDeliveredMark_OnceEveryObserverReturned()
    {
        await using var rig = await Rig.StartAsync(capacity: 1);
        var (_, after) = await rig.DeliverAcrossADropAsync("equal");
        var parked = rig.ParkInObserver("OBSERVED/equal");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "OBSERVED/equal")]);
        parked.Entered.Wait(Bound).Should().BeTrue("premise: the observer has entered");
        parked.Release();
        await rig.HandledAsync("OBSERVED/equal");
        var delivered = rig.Connection.DeliveredLossMark;
        var applied = rig.Connection.AppliedLossMark;

        using (new AssertionScope())
        {
            delivered.Should().Be(after, "premise: the observed event is the last one dispatched");
            applied.Should().Be(delivered, "every observer of that event has returned");
        }
    }

    [Fact]
    public async Task DeliveredLossMark_ShouldReadTheStartedEvent_WhileAHandlerIsStillAwaited()
    {
        await using var rig = await Rig.StartAsync(capacity: 1);
        var (before, after) = await rig.DeliverAcrossADropAsync("awaited");
        var held = rig.HoldInHandler("HOLD/awaited");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "HOLD/awaited")]);
        await held.Entered.WaitAsync(Bound);
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "QUEUED/awaited")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/lost-awaited-2"), new("Uniqueid", "8.2")]);
        await rig.FenceAsync();
        var inDispatch = rig.Connection.InDispatch;
        var readerSide = rig.Connection.EventLossMark;
        var delivered = rig.Connection.DeliveredLossMark;
        var applied = rig.Connection.AppliedLossMark;
        held.Release();

        using (new AssertionScope())
        {
            before.Should().NotBe(after, "premise: a drop lies between the earlier events");
            inDispatch.Should().BeFalse("premise: the test thread is not inside the handed-off dispatch");
            readerSide.Should().Be(after with { DroppedBefore = after.DroppedBefore + 1 },
                "premise: a second drop arrived while the handler was awaited");
            delivered.Should().Be(after, "the awaited event's dispatch has started; its handler still runs");
            applied.Should().Be(after, "its observers returned before its handlers were awaited");
        }
    }

    [Fact]
    public async Task DeliveredLossMark_ShouldKeepTheParkedEventsMark_WhenADropAndALaterEventArriveWhileItsObserverIsParked()
    {
        await using var rig = await Rig.StartAsync(capacity: 1);
        var parked = rig.ParkInObserver("OBSERVED/parked");
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "OBSERVED/parked")]);
        parked.Entered.Wait(Bound).Should().BeTrue("premise: the parked event's observer has entered");
        var session = rig.Connection.EventLossMark.Session;
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "QUEUED/q")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/lost-p"), new("Uniqueid", "7.9")]);
        await rig.FenceAsync();
        var delivered = rig.Connection.DeliveredLossMark;
        var readerSide = rig.Connection.EventLossMark;
        var queuedDispatched = rig.WasHandled("QUEUED/q");
        parked.Release();

        using (new AssertionScope())
        {
            queuedDispatched.Should().BeFalse("the queued event is not dispatched while the parked one runs");
            readerSide.Should().Be(new AmiEventLossMark(session, 1), "premise: the reader counted the drop");
            delivered.Should().Be(new AmiEventLossMark(session, 0),
                "an off-dispatch read names the parked event, which arrived before the drop");
        }
    }

    /// <summary>A signal the blocked observer or handler sets on entry, and the gate it waits on.</summary>
    private sealed class AsyncGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task Gate => _gate.Task;

        public void Enter() => _entered.TrySetResult();

        public void Release() => _gate.TrySetResult();
    }

    /// <summary>A synchronous gate for an observer's <c>OnNext</c>, which cannot await.</summary>
    private sealed class SyncGate : IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Gate { get; } = new();

        public void Release() => Gate.Set();

        public void Dispose()
        {
            Entered.Dispose();
            Gate.Dispose();
        }
    }

    private sealed class Observer(Action<ManagerEvent> next) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => next(value);

        public void OnError(Exception error)
        {
            // The connection never faults its observers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the connection is disposed; nothing to do.
        }
    }

    /// <summary>
    /// A logged-in connection over a piped peer with an event buffer of the given capacity, an observer that parks a named
    /// PeerStatus and records both marks for the PeerStatus named <c>PROBE</c>, and an <c>OnEvent</c> handler that holds
    /// a named PeerStatus and records every PeerStatus it handled with its mark.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _peerCts = new(Bound * 6);
        private readonly Dictionary<string, AsyncGate> _handlerHolds = [];
        private readonly Dictionary<string, SyncGate> _observerParks = [];
        private readonly Dictionary<string, TaskCompletionSource<AmiEventLossMark>> _handled = [];
        private readonly Lock _sync = new();
        private readonly TaskCompletionSource<(AmiEventLossMark EventLoss, AmiEventLossMark Delivered)> _probe =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDisposable? _subscription;

        public AmiConnection Connection { get; private set; } = null!;

        public PipedSocket Peer { get; private set; } = null!;

        public Task<(AmiEventLossMark EventLoss, AmiEventLossMark Delivered)> ProbeSeen => _probe.Task;

        public Task<AmiEventLossMark> ProbeHandled => HandledAsync("PROBE");

        public static AmiConnectionOptions OptionsWith(int capacity) => new()
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
            DefaultEventTimeout = TimeSpan.Zero,
            EventPumpCapacity = capacity,
        };

        public static async Task<Rig> StartAsync(int capacity)
        {
            var rig = new Rig();
            var factory = new PipedSocketFactory();
            rig.Connection = new AmiConnection(Options.Create(OptionsWith(capacity)), factory, NullLogger<AmiConnection>.Instance);
            rig.Connection.OnEvent += rig.OnEventAsync;
            rig._subscription = rig.Connection.Subscribe(new Observer(rig.OnNext));
            var loggedIn = Task.Run(async () =>
            {
                var peer = await factory.NextAsync(rig._peerCts.Token);
                await peer.CompleteLoginAsync(rig._peerCts.Token);
                return peer;
            }, rig._peerCts.Token);
            await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
            rig.Peer = await loggedIn.WaitAsync(Bound);
            return rig;
        }

        public AsyncGate HoldInHandler(string peer)
        {
            var gate = new AsyncGate();
            lock (_sync)
                _handlerHolds[peer] = gate;
            return gate;
        }

        public SyncGate ParkInObserver(string peer)
        {
            var gate = new SyncGate();
            lock (_sync)
                _observerParks[peer] = gate;
            return gate;
        }

        public Task<AmiEventLossMark> HandledAsync(string peer) => Handled(peer).Task.WaitAsync(Bound);

        public bool WasHandled(string peer) => Handled(peer).Task.IsCompleted;

        /// <summary>
        /// Delivers one event with a drop behind it: H is held while B is buffered behind it and X is refused; once H is
        /// released, B is dispatched in full with the mark from before the drop, and the next event to arrive carries one
        /// drop more. Returns B's mark as its handler saw it.
        /// </summary>
        public async Task<AmiEventLossMark> DeliverThenDropAsync(string tag)
        {
            var held = HoldInHandler($"HOLD/{tag}/first");
            await Peer.WriteEventAsync("PeerStatus", [new("Peer", $"HOLD/{tag}/first")]);
            await held.Entered.WaitAsync(Bound);
            await Peer.WriteEventAsync("PeerStatus", [new("Peer", $"B/{tag}")]);
            await Peer.WriteEventAsync("Newstate", [new("Channel", $"PJSIP/lost-{tag}"), new("Uniqueid", "8.1")]);
            await FenceAsync();
            held.Release();
            return await HandledAsync($"B/{tag}");
        }

        /// <summary>
        /// <see cref="DeliverThenDropAsync"/>, then the next event E dispatched in full. Returns B's and E's marks as
        /// their handlers saw them.
        /// </summary>
        public async Task<(AmiEventLossMark Before, AmiEventLossMark After)> DeliverAcrossADropAsync(string tag)
        {
            var before = await DeliverThenDropAsync(tag);
            await Peer.WriteEventAsync("PeerStatus", [new("Peer", $"E/{tag}")]);
            var after = await HandledAsync($"E/{tag}");
            return (before, after);
        }

        /// <summary>A Ping answered by the peer: every event written before it has been read once it returns.</summary>
        public async Task FenceAsync()
        {
            var ping = Connection.SendActionAsync(new PingAction()).AsTask();
            var action = await Peer.ReadActionAsync(_peerCts.Token).WaitAsync(Bound);
            PipedSocket.IsPing(action).Should().BeTrue("the connection sends the Ping");
            (await Peer.RespondAsync("Success", PipedSocket.ActionIdOf(action!))).Should().BeTrue();
            await ping.WaitAsync(Bound);
        }

        private TaskCompletionSource<AmiEventLossMark> Handled(string peer)
        {
            lock (_sync)
            {
                if (!_handled.TryGetValue(peer, out var seen))
                    _handled[peer] = seen = new TaskCompletionSource<AmiEventLossMark>(TaskCreationOptions.RunContinuationsAsynchronously);
                return seen;
            }
        }

        private void OnNext(ManagerEvent evt)
        {
            var peer = evt.RawFields?.GetValueOrDefault("Peer");
            if (peer is null)
                return;
            if (peer == "PROBE")
                _probe.TrySetResult((Connection.EventLossMark, Connection.DeliveredLossMark));

            SyncGate? park;
            lock (_sync)
                _observerParks.TryGetValue(peer, out park);
            if (park is null)
                return;
            park.Entered.Set();
            park.Gate.Wait(Bound);
        }

        private async ValueTask OnEventAsync(ManagerEvent evt)
        {
            var peer = evt.RawFields?.GetValueOrDefault("Peer");
            if (peer is null)
                return;

            AsyncGate? hold;
            lock (_sync)
                _handlerHolds.TryGetValue(peer, out hold);
            if (hold is not null)
            {
                hold.Enter();
                await hold.Gate;
            }

            Handled(peer).TrySetResult(Connection.EventLossMark);
        }

        public async ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                foreach (var gate in _handlerHolds.Values)
                    gate.Release();
                foreach (var gate in _observerParks.Values)
                    gate.Release();
            }

            _subscription?.Dispose();
            await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
            await _peerCts.CancelAsync();
            _peerCts.Dispose();
            lock (_sync)
            {
                foreach (var gate in _observerParks.Values)
                    gate.Dispose();
            }
        }
    }
}
