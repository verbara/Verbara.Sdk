using System.Reflection;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The connection's reads of its last lost event: the ordinal of the last refused event no action received, whether the
/// current pump has dispatched through it, and the current loss mark — the current session and the current pump's count,
/// read the same inside and outside a dispatch, where <see cref="AmiConnection.EventLossMark"/> read inside one is the
/// mark of the event being delivered.
/// </summary>
/// <remarks>
/// <para>
/// Every test holds the session's first dispatch on a gate and fills the buffer behind it (capacity
/// <see cref="Capacity"/>), so a later event is refused. A Ping the peer answers is the fence: once it returns, the reader
/// has handed every earlier event to the buffer. No wall clock is read except <see cref="Bound"/>, a hang bound every wait
/// ends long before.
/// </para>
/// <para>
/// The class runs in <see cref="AmiEventsDroppedMetricGroup"/> because its drops move the process-wide
/// <c>ami.events.dropped</c> counter another class of that group counts.
/// </para>
/// </remarks>
[Collection(AmiEventsDroppedMetricGroup.Name)]
public sealed class AmiConnectionDeliveredLossTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const int Capacity = 2;

    private const string HeldPeer = "HOLD/delivered";

    [Fact]
    public async Task DeliveredThroughLastLoss_ShouldReadFalseUntilThePumpDispatchesThroughTheDrop_WhenNoEventFollowsIt()
    {
        await using var rig = await Rig.StartAsync();
        await rig.HoldAsync(HeldPeer);
        for (var i = 0; i < Capacity; i++)
            await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", $"FILL/{i}")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/lost-1"), new("Uniqueid", "5.1")]);
        await rig.FenceAsync();
        var pump = rig.Pump;
        var lastReceived = pump.LastReceived;
        var whileBehind = (rig.Connection.DeliveredThroughLastLoss, pump.LastLossOrdinal);

        rig.Open(HeldPeer);
        var reached = await pump.WaitDispatchedThroughAsync(lastReceived, Bound, TimeProvider.System);
        var once = rig.Connection.DeliveredThroughLastLoss;

        using (new AssertionScope())
        {
            whileBehind.Should().Be((false, lastReceived),
                "the refused Newstate is the last loss, and the events received before it are held or buffered");
            reached.Should().Be(DispatchBarrierOutcome.Reached, "premise: the pump passes a trailing drop once it is idle");
            once.Should().BeTrue("the pump has dispatched through the drop, with no event after it");
        }
    }

    [Theory]
    [InlineData("StatusComplete")]
    [InlineData("OriginateResponse")]
    public async Task LastLossOrdinal_ShouldNotMove_WhenTheFullBufferRefusesAnEventThatEndsAnActionsAnswer(string ending)
    {
        await using var rig = await Rig.StartAsync();
        await rig.HoldAsync(HeldPeer);
        for (var i = 0; i < Capacity; i++)
            await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", $"FILL/{i}")]);
        await rig.FenceAsync();

        ManagerAction action = ending == "StatusComplete"
            ? new StatusAction()
            : new OriginateAction { Channel = "Local/s@hold", Context = "hold", Exten = "s", Priority = 1, IsAsync = true };
        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(action));
        var id = await rig.ReadActionIdAsync();
        if (ending == "StatusComplete")
        {
            await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
            await rig.Peer.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "0")]);
        }
        else
        {
            await rig.Peer.RespondAsync("Success", id, [new("Message", "Originate successfully queued")]);
            await rig.Peer.WriteEventAsync("OriginateResponse", [new("ActionID", id), new("Response", "Success"),
                new("Channel", "Local/s@hold-00000001;1"), new("Uniqueid", "4.1")]);
        }
        await read.WaitAsync(Bound);
        await rig.FenceAsync();
        var pump = rig.Pump;

        using (new AssertionScope())
        {
            pump.DroppedEvents.Should().Be(1, $"premise: the full buffer refused the {ending}");
            pump.LastLossOrdinal.Should().Be(0, $"the action received its {ending}, so its refused copy is no loss");
            rig.Connection.DeliveredThroughLastLoss.Should().BeTrue("there is no loss to deliver through");
            rig.Connection.EventLossMark.DroppedBefore.Should().Be(0);
        }
    }

    [Theory]
    [InlineData("DeviceStateChange")]
    [InlineData("PresenceStateChange")]
    [InlineData("ExtensionStatus")]
    public async Task LastLossOrdinal_ShouldMove_WhenTheFullBufferRefusesAnAnswerCopyAnObserverReadsAsAStateChange(string answerType)
    {
        await using var rig = await Rig.StartAsync();
        await rig.HoldAsync(HeldPeer);
        for (var i = 0; i < Capacity; i++)
            await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", $"FILL/{i}")]);
        await rig.FenceAsync();

        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new PresenceStateListAction()));
        var id = await rig.ReadActionIdAsync();
        await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
        for (var i = 0; i < 2; i++)
            await rig.Peer.WriteEventAsync(answerType, [new("ActionID", id), new("Device", $"Custom:d-{i}"), new("State", "INUSE")]);
        await rig.FenceAsync();
        var pump = rig.Pump;
        var lastCopy = pump.LastReceived;

        using (new AssertionScope())
        {
            pump.LastLossOrdinal.Should().Be(lastCopy,
                "an observer reads the answer's copy as a state change, so its refusal is a loss and the last one is the last copy");
            rig.Connection.DeliveredThroughLastLoss.Should().BeFalse("the events received before it are held or buffered");
        }

        rig.Open(HeldPeer);
        await rig.Peer.WriteEventAsync("PresenceStateListComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "2")]);
        await read.WaitAsync(Bound);
    }

    [Fact]
    public async Task CurrentEventLossMark_ShouldReadTheCurrentPumpsCount_WhenReadInsideTheDispatchOfAnEventReceivedBeforeTheDrop()
    {
        await using var rig = await Rig.StartAsync();
        var inOnNext = new TaskCompletionSource<(AmiEventLossMark Current, AmiEventLossMark Delivered)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = rig.Connection.Subscribe(new Observer(evt =>
        {
            if (evt.RawFields?.GetValueOrDefault("Peer") == "PROBE")
                inOnNext.TrySetResult((rig.Connection.CurrentEventLossMark, rig.Connection.EventLossMark));
        }));

        await rig.HoldAsync(HeldPeer);
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "PROBE")]);
        await rig.Peer.WriteEventAsync("PeerStatus", [new("Peer", "FILL/0")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/lost-2"), new("Uniqueid", "5.2")]);
        await rig.FenceAsync();
        var session = rig.Connection.EventLossMark.Session;
        rig.Open(HeldPeer);
        var observed = await inOnNext.Task.WaitAsync(Bound);
        var handled = await rig.ProbeSeen.WaitAsync(Bound);

        using (new AssertionScope())
        {
            observed.Delivered.Should().Be(new AmiEventLossMark(session, 0),
                "premise: inside the observer the delivered event's mark is its arrival-time mark");
            observed.Current.Should().Be(new AmiEventLossMark(session, 1),
                "the current mark reads the current pump's count inside an observer's OnNext, never the dispatch frame's");
            handled.Delivered.Should().Be(new AmiEventLossMark(session, 0),
                "premise: inside the OnEvent handler the delivered event's mark is its arrival-time mark");
            handled.Current.Should().Be(new AmiEventLossMark(session, 1),
                "the current mark reads the current pump's count inside an OnEvent handler too");
        }
    }

    private static async Task<List<ManagerEvent>> ReadAllAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        var received = new List<ManagerEvent>();
        await foreach (var evt in events)
            received.Add(evt);

        return received;
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
    /// A logged-in connection over a piped peer with an event buffer of <see cref="Capacity"/>, and an <c>OnEvent</c>
    /// handler that holds the dispatch of a named PeerStatus until it is opened and records both marks for the PeerStatus
    /// named <c>PROBE</c>.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _peerCts = new(Bound * 6);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<(AmiEventLossMark Current, AmiEventLossMark Delivered)> _probe =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _held;

        public AmiConnection Connection { get; private set; } = null!;

        public PipedSocket Peer { get; private set; } = null!;

        public Task<(AmiEventLossMark Current, AmiEventLossMark Delivered)> ProbeSeen => _probe.Task;

        // Internal to the connection, read the way the other connection tests read its pump.
        public AsyncEventPump Pump => (AsyncEventPump)typeof(AmiConnection)
            .GetField("_eventPump", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Connection)!;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            var factory = new PipedSocketFactory();
            rig.Connection = new AmiConnection(Options.Create(new AmiConnectionOptions
            {
                Hostname = "localhost",
                Username = "admin",
                Password = "secret",
                EnableHeartbeat = false,
                AutoReconnect = false,
                DefaultEventTimeout = TimeSpan.Zero,
                EventPumpCapacity = Capacity,
            }), factory, NullLogger<AmiConnection>.Instance);
            rig.Connection.OnEvent += rig.OnEventAsync;
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

        /// <summary>The peer sends <paramref name="held"/>, whose dispatch the handler holds until <see cref="Open"/>.</summary>
        public async Task HoldAsync(string held)
        {
            _held = held;
            (await Peer.WriteEventAsync("PeerStatus", [new("Peer", held)])).Should().BeTrue();
            await _entered.Task.WaitAsync(Bound);
        }

        public void Open(string held)
        {
            held.Should().Be(_held);
            _gate.TrySetResult();
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

        public async Task<string> ReadActionIdAsync()
        {
            var action = await Peer.ReadActionAsync(_peerCts.Token).WaitAsync(Bound)
                ?? throw new InvalidOperationException("The session ended before the connection sent its action.");
            return PipedSocket.ActionIdOf(action);
        }

        private async ValueTask OnEventAsync(ManagerEvent evt)
        {
            var peer = evt.RawFields?.GetValueOrDefault("Peer");
            if (peer == "PROBE")
                _probe.TrySetResult((Connection.CurrentEventLossMark, Connection.EventLossMark));

            if (peer is not null && peer == _held)
            {
                _entered.TrySetResult();
                await _gate.Task;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _gate.TrySetResult();
            await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
            await _peerCts.CancelAsync();
            _peerCts.Dispose();
        }
    }
}
