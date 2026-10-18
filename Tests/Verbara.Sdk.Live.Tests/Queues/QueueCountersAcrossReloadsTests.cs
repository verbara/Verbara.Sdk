using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// Live's queue counters follow the queue table's transitions across a reconnect's reload: a caller already held and
/// listed again is a replacement and counts nothing; a caller the completed snapshot no longer lists, or whose queue it
/// no longer lists, or whose queue is removed, counts <c>left</c> with no wait sample; and an event the pump still
/// holds when <c>QueueStatusComplete</c> arrives is applied once, before the snapshot completes. So
/// <c>joined − left</c> equals the callers held, summed over the process.
/// </summary>
/// <remarks>
/// <para>
/// The first five tests drive the reload through a substitute connection, raising <c>Reconnected</c> as many times as
/// they reload and waiting on the server's <c>[LIVE] State loaded</c> line for each; the live joins reach the server
/// through its own event observer, as the connection delivers them, built with the headers Asterisk sends
/// (<see cref="AsteriskFrames"/>). The last two need the connection's real pump, so they run a real
/// <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/> with one reconnect, and hold its
/// dispatch with a <see cref="DispatchGate"/>.
/// </para>
/// <para>
/// The counters are read with <see cref="LiveCounters"/>, so every class here runs in <see cref="LiveCountersGroup"/>.
/// </para>
/// </remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class QueueCountersAcrossReloadsTests
{
    private const string Queue = BootingAsterisk.QueueName;
    private const string StateLoaded = "[LIVE] State loaded";

    private static string Uid(int i) => $"1790633830.{i}";

    private static string Chan(int i) => $"PJSIP/pstn-{i:D8}";

    [Fact]
    public async Task Reload_ShouldKeepJoinedMinusLeftAtTheCallersHeld_WhenEveryReloadListsTheSameCallers()
    {
        const int held = 3;
        const int reloads = 4;
        await using var rig = new SubstituteRig();
        await rig.StartAsync();
        using var counters = new LiveCounters();
        for (var i = 0; i < held; i++)
            rig.LiveJoin(Uid(i), Chan(i), position: i + 1);

        rig.QueueStatus = [SubstituteRig.Params(), .. Enumerable.Range(0, held).Select(i => SubstituteRig.Entry(Uid(i), Chan(i), i + 1, wait: 5))];
        for (var r = 0; r < reloads; r++)
            await rig.ReloadAsync();

        using (new AssertionScope())
        {
            (counters.Joined - counters.Left).Should().Be(held,
                $"{held} callers are held after {reloads} reloads that each list the same {held}; joined − left counts the "
                + $"callers held, not the reloads. Measured: {counters}");
            rig.Server.Queues.GetByName(Queue)!.EntryCount.Should().Be(held);
        }
    }

    [Fact]
    public async Task Reload_ShouldCountACallerLeftWithNoWaitSample_WhenTheSnapshotNoLongerListsIt()
    {
        await using var rig = new SubstituteRig();
        await rig.StartAsync();
        using var counters = new LiveCounters();
        rig.LiveJoin(Uid(0), Chan(0), position: 1);
        rig.LiveJoin(Uid(1), Chan(1), position: 2);

        rig.QueueStatus = [SubstituteRig.Params(), SubstituteRig.Entry(Uid(1), Chan(1), position: 1, wait: 5)];
        await rig.ReloadAsync();

        new
        {
            Left = counters.Left,
            WaitSamples = counters.WaitSamples,
            JoinedMinusLeft = counters.Joined - counters.Left,
            Held = rig.Server.Queues.GetByName(Queue)!.Entries.Keys.Order().ToArray(),
        }.Should().BeEquivalentTo(
            new { Left = 1L, WaitSamples = 0L, JoinedMinusLeft = 1L, Held = new[] { Chan(1) } },
            "the caller the completed snapshot no longer lists is counted left, with no wait sample since its leave was "
            + $"not observed, and the one it lists is still held. Measured: {counters}");
    }

    [Fact]
    public async Task Reload_ShouldKeepTheHeldEntryAndUpdateOnlyItsPosition_WhenTheSnapshotListsAHeldCaller()
    {
        await using var rig = new SubstituteRig();
        await rig.StartAsync();
        rig.LiveJoin(Uid(0), Chan(0), position: 2);
        var before = rig.Server.Queues.GetByName(Queue)!.Entries[Chan(0)];
        var was = (before.JoinedAt, before.FromSnapshot, before.ReportedWaitSeconds, before.Position);
        using var counters = new LiveCounters();

        rig.QueueStatus = [SubstituteRig.Params(), SubstituteRig.Entry(Uid(0), Chan(0), position: 1, wait: 42)];
        await rig.ReloadAsync();

        var after = rig.Server.Queues.GetByName(Queue)!.Entries[Chan(0)];
        new
        {
            after.JoinedAt,
            after.FromSnapshot,
            after.ReportedWaitSeconds,
            after.Position,
            Joined = counters.Joined,
        }.Should().BeEquivalentTo(
            new { was.JoinedAt, was.FromSnapshot, was.ReportedWaitSeconds, Position = 1, Joined = 0L },
            "a caller already held and listed again is a replacement: JoinedAt, FromSnapshot and ReportedWaitSeconds stay "
            + $"as the live join set them, only Position follows the snapshot, and nothing is counted. Was: {was}");
    }

    [Fact]
    public void RemoveQueue_ShouldCountEveryCallerLeftWithNoWaitSample_WhenTheQueueHoldsCallers()
    {
        var queues = new QueueManager(NullLogger.Instance);
        queues.OnCallerJoined(Queue, Chan(0), "5552101", 1);
        queues.OnCallerJoined(Queue, Chan(1), "5552101", 2);
        using var counters = new LiveCounters();

        var removed = queues.RemoveQueue(Queue);

        new { Removed = removed, Left = counters.Left, WaitSamples = counters.WaitSamples }.Should().BeEquivalentTo(
            new { Removed = true, Left = 2L, WaitSamples = 0L },
            "removing a queue removes its two callers, and each removal counts left with no wait sample, since no leave "
            + $"was observed. Measured: {counters}");
    }

    [Fact]
    public async Task Reload_ShouldCountTheCallersOfAQueueTheSnapshotOmitsLeftWithNoWaitSample_WhenTheQueueIsGone()
    {
        const string goneQueue = "q-gone";
        await using var rig = new SubstituteRig();
        await rig.StartAsync();
        using var counters = new LiveCounters();
        rig.LiveJoin(Uid(0), Chan(0), position: 1, queue: goneQueue);
        rig.LiveJoin(Uid(1), Chan(1), position: 2, queue: goneQueue);

        rig.QueueStatus = [SubstituteRig.Params()];
        await rig.ReloadAsync();

        new
        {
            GoneQueueHeld = rig.Server.Queues.GetByName(goneQueue) is not null,
            Left = counters.Left,
            WaitSamples = counters.WaitSamples,
            JoinedMinusLeft = counters.Joined - counters.Left,
        }.Should().BeEquivalentTo(
            new { GoneQueueHeld = false, Left = 2L, WaitSamples = 0L, JoinedMinusLeft = 0L },
            "a queue the completed QueueStatus no longer lists is removed, and each of its two callers counts left with no "
            + $"wait sample. Measured: {counters}");
    }

    [Fact]
    public async Task Reload_ShouldCountAQueuedLeaveOnceWithItsWaitSample_WhenItWaitsInThePumpAsQueueStatusCompletes()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var statusAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = statusAnswer.Task };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        using var gate = new DispatchGate();
        using var gateSubscription = run.Connection.Subscribe(gate);
        await run.StartServerAsync();
        var joined = Signal<AsteriskQueueEntry>(h => run.Server.Queues.CallerJoined += (_, e) => h(e));
        await first.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Uid(0), Chan(0), Queue, 1, 1));
        await joined.WaitAsync(Run.Bound);
        var leaveReported = Signal<QueueCallerLeaveReport>(h => run.Server.Queues.CallerLeaveReported += h);
        using var counters = new LiveCounters();

        // Asterisk no longer lists the caller: its leave was sent before QueueStatus was asked for.
        second.QueueEntries = [];
        first.CloseSession();
        await run.SecondSocketCreated.WaitAsync(Run.Bound);
        run.ReleaseSecondPeer();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await second.SendEventAsync("UserEvent", DispatchGate.Frame());
        await second.SendEventAsync("QueueCallerLeave", AsteriskFrames.QueueCallerLeave(Uid(0), Chan(0), Queue, 1, 0));
        await gate.Held.WaitAsync(Run.Bound);
        statusAnswer.TrySetResult();

        await ReloadEndsOrWaitsOnItsBarrierAsync(run);
        gate.Release();
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        await leaveReported.WaitAsync(Run.Bound);

        new
        {
            Left = counters.Left,
            WaitSamples = counters.WaitSamples,
            Held = run.Server.Queues.GetByName(Queue)?.EntryCount ?? 0,
            GateTimedOut = gate.TimedOut,
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new { Left = 1L, WaitSamples = 1L, Held = 0, GateTimedOut = false, PeerFault = default(string) },
            "the leave waited in the pump while QueueStatusComplete reached the load; it is applied before the snapshot "
            + $"completes, so it is counted once, with its wait sample. Measured: {counters}");
    }

    [Fact]
    public async Task Reload_ShouldCountAQueuedJoinOnce_WhenItWaitsInThePumpAsQueueStatusCompletes()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var statusAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = statusAnswer.Task };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        using var gate = new DispatchGate();
        using var gateSubscription = run.Connection.Subscribe(gate);
        await run.StartServerAsync();
        var liveJoined = Signal<AsteriskQueueEntry>(h => run.Server.Queues.CallerJoined += (_, e) =>
        {
            if (!e.FromSnapshot)
                h(e);
        });
        using var counters = new LiveCounters();

        // Asterisk lists the caller: its join was sent before QueueStatus was asked for.
        second.QueueEntries = [new QueueCaller(Chan(0), Uid(0), Position: 1)];
        first.CloseSession();
        await run.SecondSocketCreated.WaitAsync(Run.Bound);
        run.ReleaseSecondPeer();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await second.SendEventAsync("UserEvent", DispatchGate.Frame());
        await second.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Uid(0), Chan(0), Queue, 1, 1));
        await gate.Held.WaitAsync(Run.Bound);
        statusAnswer.TrySetResult();

        await ReloadEndsOrWaitsOnItsBarrierAsync(run);
        gate.Release();
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        await liveJoined.WaitAsync(Run.Bound);

        new
        {
            Joined = counters.Joined,
            Held = run.Server.Queues.GetByName(Queue)?.EntryCount ?? 0,
            GateTimedOut = gate.TimedOut,
            PeerFault = second.Fault?.Message,
        }.Should().BeEquivalentTo(
            new { Joined = 1L, Held = 1, GateTimedOut = false, PeerFault = default(string) },
            "one caller joined once: the live join that waited in the pump is applied before the snapshot completes, and "
            + $"the snapshot's entry for the same caller is then a replacement. Measured: {counters}");
    }

    /// <summary>
    /// Waits until the reconnect's reload has either ended (unfixed code: nothing makes it wait for the pump) or begun to
    /// wait on its barrier, which arms the barrier's bound as a timer on the server's clock. Either way the
    /// test may then release the dispatch.
    /// </summary>
    private static async Task ReloadEndsOrWaitsOnItsBarrierAsync(Run run)
    {
        using var stop = new CancellationTokenSource();
        var loaded = run.ServerLog.Logged(StateLoaded, 2);
        var timer = run.Clock.TimersCreated.ReadAsync(stop.Token).AsTask();
        await Task.WhenAny(loaded, timer).WaitAsync(Run.Bound);
        await stop.CancelAsync();
    }

    /// <summary>A task that completes on the first call of the handler <paramref name="subscribe"/> registers.</summary>
    private static Task Signal<T>(Action<Action<T>> subscribe)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(_ => signal.TrySetResult());
        return signal.Task;
    }

    /// <summary>
    /// A <see cref="VerbaraServer"/> over a substitute connection that answers <c>QueueStatus</c> with
    /// <see cref="QueueStatus"/> and every other list with nothing, and reloads when the test raises <c>Reconnected</c>.
    /// </summary>
    [SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "The substitute owns nothing.")]
    private sealed class SubstituteRig : IAsyncDisposable
    {
        private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
        private IObserver<ManagerEvent>? _observer;
        private int _loads;

        public SubstituteRig()
        {
            _connection.AsteriskVersion.Returns("22.9.0");
            _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs))
                .Returns(_ => Substitute.For<IDisposable>());
            _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
                .Returns(call => Answer(call.Arg<ManagerAction>() is QueueStatusAction ? QueueStatus : []));
            Server = new VerbaraServer(_connection, Log);
        }

        public SignalingLogger<VerbaraServer> Log { get; } = new();

        public VerbaraServer Server { get; }

        /// <summary>What <c>QueueStatus</c> answers, in order. Settable between two loads.</summary>
        public ManagerEvent[] QueueStatus { get; set; } = [];

        public async Task StartAsync()
        {
            await Server.StartAsync().WaitAsync(Run.Bound);
            await Log.Logged(StateLoaded, ++_loads).WaitAsync(Run.Bound);
        }

        /// <summary>Raises <c>Reconnected</c> and waits for the reload's <c>[LIVE] State loaded</c>.</summary>
        public async Task ReloadAsync()
        {
            _connection.Reconnected += Raise.Event<Action>();
            await Log.Logged(StateLoaded, ++_loads).WaitAsync(Run.Bound);
        }

        /// <summary>A live <c>QueueCallerJoin</c>, delivered through the server's own observer.</summary>
        public void LiveJoin(string uniqueId, string channel, int position, string queue = Queue) =>
            _observer!.OnNext(new QueueCallerJoinEvent
            {
                Position = position,
                UniqueId = uniqueId,
                RawFields = AsteriskFrames.QueueCallerJoin(uniqueId, channel, queue, position, position)
                    .ToDictionary(f => f.Key, f => f.Value),
            });

        public static QueueParamsEvent Params() => new()
        {
            Queue = Queue, Max = 0, Strategy = "ringall", Calls = 0, HoldTime = 0, TalkTime = 0, Completed = 0,
            Abandoned = 0,
        };

        public static QueueEntryEvent Entry(string uniqueId, string channel, int position, long wait) => new()
        {
            Queue = Queue, Position = position, Channel = channel, Uniqueid = uniqueId, CallerIDNum = "5552101",
            CallerIDName = "QRA", Wait = wait, Priority = 0,
        };

        public async ValueTask DisposeAsync() => await Server.DisposeAsync();

        private static async IAsyncEnumerable<ManagerEvent> Answer(ManagerEvent[] events)
        {
            await Task.CompletedTask;
            foreach (var evt in events)
                yield return evt;
        }
    }
}
