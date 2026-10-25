using System.Diagnostics.Metrics;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A full event buffer that drops the copy of an event an action had already received in its answer has lost nothing:
/// the action holds the event, and the copy is not a loss. The connection's event-loss mark moves only for a dropped
/// event no action received, while <c>ami.events.dropped</c> keeps counting every refused event.
/// </summary>
/// <remarks>
/// <para>
/// Every test holds the session's first dispatch on a gate and fills the buffer behind it (capacity
/// <see cref="Capacity"/>), so every later event is refused. Whatever the peer writes is read before a Ping is answered,
/// so the Ping is the fence: once it returns, the reader has handed every earlier event to the buffer. No wall clock is
/// read except <see cref="Bound"/>, a hang bound every wait ends long before.
/// </para>
/// <para>
/// The class runs in <see cref="AmiEventsDroppedMetricGroup"/> because it listens on the process-wide
/// <c>ami.events.dropped</c> counter.
/// </para>
/// </remarks>
[Collection(AmiEventsDroppedMetricGroup.Name)]
public sealed class AmiConnectionCollectedDropTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const int Capacity = 2;

    private const string HeldPeer = "HOLD/collected";

    [Fact]
    public async Task EventLossMark_ShouldNotMove_WhenTheFullBufferDropsCopiesOfAnAnswerTheActionReceived()
    {
        using var drops = new BufferFullDrops();
        await using var rig = await DropRig.StartAsync();
        await rig.HoldAndFillAsync(HeldPeer);

        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new StatusAction()));
        var id = await rig.ReadActionIdAsync();
        await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
        for (var i = 0; i < 3; i++)
            await rig.Peer.WriteEventAsync("Status", [new("ActionID", id), new("Channel", $"PJSIP/s-{i}"), new("Uniqueid", $"9.{i}")]);
        await rig.FenceAsync();

        var markAfterDrops = rig.Connection.EventLossMark;
        var countedAfterDrops = drops.Total;

        rig.Open(HeldPeer);
        await rig.CountReachedAsync(1 + Capacity);
        await rig.Peer.WriteEventAsync("Hangup", [new("Channel", "PJSIP/after-1"), new("Uniqueid", "8.1"), new("Cause", "16")]);
        await rig.Peer.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "3")]);
        var received = await read.WaitAsync(Bound);
        await rig.FenceAsync();
        await rig.CountReachedAsync(1 + Capacity + 2);

        using (new AssertionScope())
        {
            received.Select(e => e.EventType).Should().Equal(["Status", "Status", "Status"],
                "premise: the action received its three entries, whatever the buffer did with their copies");
            countedAfterDrops.Should().Be(3, "the buffer refused the three copies, and the metric counts each refused event");
            markAfterDrops.DroppedBefore.Should().Be(0,
                "the three dropped events were copies of an answer the action had received: nothing was lost");
            rig.MarkSeenBy("Hangup").Should().NotBeNull("premise: the handler ran for the Hangup written after the drops");
            rig.MarkSeenBy("Hangup")!.Value.DroppedBefore.Should().Be(0,
                "an observer that compares the mark of an event after the drops with one before reads no loss");
        }
    }

    [Fact]
    public async Task EventLossMark_ShouldMove_WhenTheFullBufferDropsEventsNoActionReceived()
    {
        using var drops = new BufferFullDrops();
        await using var rig = await DropRig.StartAsync();
        await rig.HoldAndFillAsync(HeldPeer);

        for (var i = 0; i < 3; i++)
            await rig.Peer.WriteEventAsync("Newstate", [new("Channel", $"PJSIP/u-{i}"), new("Uniqueid", $"7.{i}")]);
        await rig.Peer.WriteEventAsync("Status", [new("ActionID", "no-such-action"), new("Channel", "PJSIP/u-9"), new("Uniqueid", "7.9")]);
        await rig.FenceAsync();

        var markAfterDrops = rig.Connection.EventLossMark;
        var countedAfterDrops = drops.Total;

        rig.Open(HeldPeer);
        await rig.CountReachedAsync(1 + Capacity);
        await rig.Peer.WriteEventAsync("Hangup", [new("Channel", "PJSIP/after-2"), new("Uniqueid", "8.2"), new("Cause", "16")]);
        await rig.FenceAsync();
        await rig.CountReachedAsync(1 + Capacity + 1);

        using (new AssertionScope())
        {
            countedAfterDrops.Should().Be(4);
            markAfterDrops.DroppedBefore.Should().Be(4,
                "no action received any of the four dropped events, the Status among them, whose ActionID no action " +
                "is waiting for: each one is lost");
            rig.MarkSeenBy("Hangup")!.Value.DroppedBefore.Should().Be(4);
        }
    }

    [Fact]
    public async Task EventLossMark_ShouldCountOnlyTheEventsNoActionReceived_WhenTheFullBufferDropsBothKinds()
    {
        using var drops = new BufferFullDrops();
        await using var rig = await DropRig.StartAsync();
        await rig.HoldAndFillAsync(HeldPeer);

        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new StatusAction()));
        var id = await rig.ReadActionIdAsync();
        await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
        await rig.Peer.WriteEventAsync("Status", [new("ActionID", id), new("Channel", "PJSIP/m-0"), new("Uniqueid", "6.0")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/m-1"), new("Uniqueid", "6.1")]);
        await rig.Peer.WriteEventAsync("Status", [new("ActionID", id), new("Channel", "PJSIP/m-2"), new("Uniqueid", "6.2")]);
        await rig.Peer.WriteEventAsync("Newstate", [new("Channel", "PJSIP/m-3"), new("Uniqueid", "6.3")]);
        await rig.Peer.WriteEventAsync("Status", [new("ActionID", id), new("Channel", "PJSIP/m-4"), new("Uniqueid", "6.4")]);
        await rig.FenceAsync();

        var markAfterDrops = rig.Connection.EventLossMark;
        var countedAfterDrops = drops.Total;

        rig.Open(HeldPeer);
        await rig.CountReachedAsync(1 + Capacity);
        await rig.Peer.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "3")]);
        var received = await read.WaitAsync(Bound);

        using (new AssertionScope())
        {
            received.Should().HaveCount(3, "premise: the action received its three entries");
            countedAfterDrops.Should().Be(5, "the metric counts all five refused events");
            markAfterDrops.DroppedBefore.Should().Be(2,
                "of the five dropped events, the three Status entries were copies the action had received; the two " +
                "Newstate events were lost");
        }
    }

    [Theory]
    [InlineData("DeviceStateChange")]
    [InlineData("PresenceStateChange")]
    [InlineData("ExtensionStatus")]
    public async Task EventLossMark_ShouldMove_WhenTheDroppedCopiesOfAnAnswerCarryTheNameOfAnUnsolicitedEvent(string answerType)
    {
        using var drops = new BufferFullDrops();
        await using var rig = await DropRig.StartAsync();
        await rig.HoldAndFillAsync(HeldPeer);

        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new PresenceStateListAction()));
        var id = await rig.ReadActionIdAsync();
        await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
        for (var i = 0; i < 2; i++)
            await rig.Peer.WriteEventAsync(answerType, [new("ActionID", id), new("Device", $"Custom:d-{i}"), new("State", "INUSE")]);
        await rig.FenceAsync();

        var markAfterDrops = rig.Connection.EventLossMark;
        var countedAfterDrops = drops.Total;

        rig.Open(HeldPeer);
        await rig.CountReachedAsync(1 + Capacity);
        await rig.Peer.WriteEventAsync("PresenceStateListComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "2")]);
        var received = await read.WaitAsync(Bound);

        using (new AssertionScope())
        {
            received.Should().HaveCount(2, "premise: the action received its two entries");
            countedAfterDrops.Should().Be(2);
            markAfterDrops.DroppedBefore.Should().Be(2,
                "an observer cannot tell an answer's copy of an event Asterisk also sends unsolicited from a state change, " +
                "and reads it as one: its dropped copy is a real loss for that observer");
        }
    }

    private static async Task<List<ManagerEvent>> ReadAllAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        var received = new List<ManagerEvent>();
        await foreach (var evt in events)
            received.Add(evt);

        return received;
    }

    /// <summary>Every event dropped as a full buffer, process-wide, while this capture is alive.</summary>
    private sealed class BufferFullDrops : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public BufferFullDrops()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ami" && instrument.Name == "ami.events.dropped")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value as string == "buffer_full")
                        Interlocked.Add(ref _total, value);
                }
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// A logged-in connection over a piped peer with an event buffer of <see cref="Capacity"/>, and an <c>OnEvent</c>
    /// handler that holds the dispatch of a named PeerStatus until it is opened, counts every dispatch, and records the
    /// event-loss mark it read for each event type.
    /// </summary>
    private sealed class DropRig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _peerCts = new(Bound * 6);
        private readonly Lock _sync = new();
        private readonly Dictionary<string, (TaskCompletionSource Entered, TaskCompletionSource Gate)> _holds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AmiEventLossMark> _marks = new(StringComparer.Ordinal);
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _count;

        public AmiConnection Connection { get; private set; } = null!;

        public PipedSocket Peer { get; private set; } = null!;

        public static async Task<DropRig> StartAsync()
        {
            var rig = new DropRig();
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

        /// <summary>The peer sends <paramref name="held"/>, whose dispatch the handler holds, then fills the buffer.</summary>
        public async Task HoldAndFillAsync(string held)
        {
            var entered = Hold(held).Entered.Task;
            (await Peer.WriteEventAsync("PeerStatus", [new("Peer", held)])).Should().BeTrue();
            await entered.WaitAsync(Bound);
            for (var i = 0; i < Capacity; i++)
                (await Peer.WriteEventAsync("PeerStatus", [new("Peer", $"FILL/{i}")])).Should().BeTrue();
            await FenceAsync();
        }

        public void Open(string held) => Hold(held).Gate.TrySetResult();

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

        /// <summary>Completes once <paramref name="count"/> dispatches have entered the handler.</summary>
        public Task CountReachedAsync(int count)
        {
            lock (_sync)
            {
                if (_count >= count)
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, signal));
                return signal.Task.WaitAsync(Bound);
            }
        }

        public AmiEventLossMark? MarkSeenBy(string eventType)
        {
            lock (_sync)
                return _marks.TryGetValue(eventType, out var mark) ? mark : null;
        }

        private (TaskCompletionSource Entered, TaskCompletionSource Gate) Hold(string peer)
        {
            lock (_sync)
            {
                if (!_holds.TryGetValue(peer, out var hold))
                {
                    hold = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    _holds[peer] = hold;
                }

                return hold;
            }
        }

        private async ValueTask OnEventAsync(ManagerEvent evt)
        {
            var mark = Connection.EventLossMark;
            List<TaskCompletionSource> met = [];
            lock (_sync)
            {
                _count++;
                _marks[evt.EventType ?? ""] = mark;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Count <= _count)
                    {
                        met.Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var signal in met)
                signal.TrySetResult();

            var peer = evt.RawFields is not null && evt.RawFields.TryGetValue("Peer", out var p) ? p : null;
            if (peer is not null && peer.StartsWith("HOLD/", StringComparison.Ordinal))
            {
                var hold = Hold(peer);
                hold.Entered.TrySetResult();
                await hold.Gate.Task;
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                foreach (var hold in _holds.Values)
                    hold.Gate.TrySetResult();
            }

            await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
            await _peerCts.CancelAsync();
            _peerCts.Dispose();
        }
    }
}
