using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Live.Tests.Queues;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// <see cref="VerbaraServer.ReconcileQueuesAsync"/>: the queue part of a load and nothing else. Asterisk announces no
/// queue it creates after Live's load, by a reload of its static configuration or a realtime insert; the explicit
/// refresh finds it, with its members and waiting callers, without reconnecting and without asking for the channels,
/// the bridges, the conference rooms or the agents. It reconciles under the load's rules: a held caller it still lists
/// keeps its entry, its join instant and its one counted join; a refused, cancelled or cut-short read removes nothing.
/// </summary>
/// <remarks>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a <see cref="VerbaraServer"/> through
/// <see cref="Run"/>, over a <see cref="BootingAsterisk"/> that has finished starting. On the AMI wire a queue created by
/// a static reload and one inserted in realtime answer <c>QueueStatus</c> the same way, so the peer plays both as a queue
/// listed after the load; which of them a real Asterisk does list is a separate measurement. In the counter
/// collection, because the tests count joins and leaves exactly.
/// </remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class VerbaraServerReconcileQueuesTests
{
    private const string Sales = "sales";
    private const string Support = "support";
    private static readonly QueueCaller Waiting = new("PJSIP/far-00000011", "1790633900.11", Position: 1, Wait: 4);
    private static readonly QueueCaller HeldCaller = new("PJSIP/far-00000001", "1790633900.1", Position: 1, Wait: 2);

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldHoldTheNewQueuesTheirMembersAndCallers_WhenTheyWereCreatedAfterTheLoadWithoutAnEvent()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var asked = Asks(peer);

        // A static queue added by a reload, and a realtime one: Asterisk sends no event for either.
        peer.OtherQueues =
        [
            new ListedQueue(Sales, ["PJSIP/2001", "PJSIP/2002"], [Waiting]),
            new ListedQueue(Support, ["PJSIP/3001"], [], Strategy: "leastrecent"),
        ];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        var sales = Held(run, Sales, "the refresh holds the queue Asterisk lists and Live did not");
        var support = Held(run, Support, "the second created queue is held too");
        using (new AssertionScope())
        {
            sales.Strategy.Should().Be("rrmemory", "with the configuration the listing reports");
            sales.Members.Keys.Should().BeEquivalentTo(["PJSIP/2001", "PJSIP/2002"], "and its listed members");
            sales.Entries.Keys.Should().Equal([Waiting.Channel], "and the caller waiting in it");
            support.Strategy.Should().Be("leastrecent");
            support.Members.Keys.Should().Equal(["PJSIP/3001"]);
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().NotBeNull("the queue of the load is kept");
            Asks(peer).Should().Be(asked with { QueueStatus = asked.QueueStatus + 1 },
                "the refresh asks QueueStatus once and nothing else: no Status, BridgeList, ConfbridgeListRooms or Agents");
            peer.QueueStatusFilters.Should().HaveCount(2).And.OnlyContain(f => f == null,
                "the refresh asks every queue, as the load does");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
            run.Connection.State.Should().Be(AmiConnectionState.Connected, "nothing reconnected");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldKeepTheSameEntryItsJoinInstantAndItsOneCountedJoin_WhenTheRefreshStillListsTheCaller()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        var held = run.Server.Queues.GetByName(BootingAsterisk.QueueName)!.Entries[HeldCaller.Channel];
        var joinedAt = held.JoinedAt;
        using var counters = new LiveCounters();

        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        var after = run.Server.Queues.GetByName(BootingAsterisk.QueueName)!.Entries[HeldCaller.Channel];
        using (new AssertionScope())
        {
            after.Should().BeSameAs(held, "a caller the refresh still lists keeps the entry Live holds");
            after.JoinedAt.Should().Be(joinedAt, "its join instant is kept");
            after.FromSnapshot.Should().BeTrue("its provenance is the load's snapshot, unchanged");
            after.ReportedWaitSeconds.Should().Be(HeldCaller.Wait, "and so is the wait Asterisk reported then");
            counters.Joined.Should().Be(0, "no join is counted for a caller already held");
            counters.Left.Should().Be(0, "and no leave");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
            run.Server.Queues.GetByName(Sales).Should().NotBeNull("the refresh still admitted the new queue");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRemoveTheCallerAndTheQueueAsALoadDoes_WhenTheCompletedRefreshNoLongerListsThem()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        var held = run.Server.Queues.GetByName(BootingAsterisk.QueueName)!.Entries[HeldCaller.Channel];
        using var counters = new LiveCounters();

        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().BeNull(
                "a completed listing of every queue that omits one is evidence that it is gone, as in a load");
            held.LeaveNotObserved.Should().BeTrue("its caller left with no leave observed");
            counters.Left.Should().Be(1, "and is counted left once");
            counters.WaitSamples.Should().Be(0, "with no wait sample, since no leave was observed");
            run.Server.Queues.GetByName(Sales).Should().NotBeNull();
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRemoveNothingAndNotThrow_WhenAsteriskRefusesQueueStatusAfterSomeRows()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        using var counters = new LiveCounters();

        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        peer.QueueStatusRefusalAfterRows = BootingAsterisk.PermissionDenied;
        var outcome = await Record.ExceptionAsync(() => run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound));

        AssertNothingRemoved(run, peer, counters, outcome, expectFailure: null);
        run.Server.Queues.GetByName(Sales).Should().BeNull("a refused listing applies none of its rows");
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRemoveNothingAndThrowOperationCanceled_WhenTheTokenIsCancelledDuringTheRead()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        using var counters = new LiveCounters();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MainQueueListed = false;
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        using var cts = new CancellationTokenSource();
        try
        {
            var refresh = run.Server.ReconcileQueuesAsync(cts.Token).AsTask();
            (await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))).Should().BeTrue("the refresh asked");
            await cts.CancelAsync();
            answer.SetResult();
            var outcome = await Record.ExceptionAsync(() => refresh.WaitAsync(Run.Bound));

            AssertNothingRemoved(run, peer, counters, outcome, expectFailure: typeof(OperationCanceledException));
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRemoveNothingAndThrowNotConnected_WhenTheSessionEndsDuringTheRead()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        using var counters = new LiveCounters();

        peer.MainQueueListed = false;
        peer.Close = PeerClose.WhenAsked;
        peer.CloseWhenAsked = "QueueStatus";
        var outcome = await Record.ExceptionAsync(() => run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            outcome.Should().BeOfType<AmiNotConnectedException>("a read its session cut short reconciles nothing");
            Held(run, BootingAsterisk.QueueName, "a cut-short read removes nothing").Entries.Should().ContainKey(HeldCaller.Channel);
            Held(run, BootingAsterisk.QueueName, "a cut-short read removes nothing").Members.Should().ContainKey(BootingAsterisk.MemberLocation);
            counters.Left.Should().Be(0, "no caller is counted left");
            run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0, "the read's window is closed");
            run.Server.Queues.HeldUniqueIdCounts().Should().BeEquivalentTo(
                new Dictionary<string, int> { [HeldCaller.UniqueId] = 1 }, "the held-caller record is unchanged");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldOnlyAdmitAndAnnounceLaterWithoutAFullReload_WhenItsWaitForThePumpRunsOut()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        var barrierClock = new FakeTimeProvider(run.Clock.GetUtcNow());
        run.Server.BarrierClock = barrierClock;
        var announced = new TaskCompletionSource<AsteriskQueueEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string, AsteriskQueueEntry> onJoined = (queue, entry) =>
        {
            if (queue == Sales)
                announced.TrySetResult(entry);
        };
        run.Server.Queues.CallerJoined += onJoined;
        try
        {
            var reloadBefore = run.Server.ScheduledReload;
            var asked = Asks(peer);
            using var counters = new LiveCounters();

            // The pump is held on an event received before the listing, so the refresh's wait for it runs out.
            await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
            await gate.Held.WaitAsync(Run.Bound);
            peer.MainQueueListed = false;
            peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [Waiting])];
            var refresh = run.Server.ReconcileQueuesAsync().AsTask();
            var timer = await barrierClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
            barrierClock.Advance(timer.DueTime);
            await refresh.WaitAsync(Run.Bound);

            using (new AssertionScope())
            {
                Held(run, Sales, "a wait that ran out still admits what the listing holds").Entries.Keys
                    .Should().Equal([Waiting.Channel]);
                var main = Held(run, BootingAsterisk.QueueName, "a wait that ran out removes no queue the listing omits");
                main.Entries.Should().ContainKey(HeldCaller.Channel, "nor a caller");
                main.Members.Should().ContainKey(BootingAsterisk.MemberLocation, "nor a member");
                counters.Left.Should().Be(0, "no caller is counted left");
                HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
                announced.Task.IsCompleted.Should().BeFalse("the admitted caller is announced only once the pump caught up");
                run.Server.ScheduledReload.Should().BeSameAs(reloadBefore, "a queue refresh schedules no full reload");
                run.ServerLog.Entries.Should().NotContain(e => e.Line.Contains("[LIVE] A load's wait for the event pump ran out"),
                    "and does not announce one");
                run.ServerLog.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                    && e.Line.Contains("QueueStatus barrier ran out"), "the wait that ran out is a Warning");
            }

            gate.Release();
            var entry = await announced.Task.WaitAsync(Run.Bound);

            using (new AssertionScope())
            {
                entry.Channel.Should().Be(Waiting.Channel, "the admitted caller is announced once the pump caught up");
                entry.FromSnapshot.Should().BeTrue();
                Asks(peer).Should().Be(asked with { QueueStatus = asked.QueueStatus + 1 },
                    "nothing but QueueStatus was asked: no Status, BridgeList, ConfbridgeListRooms or Agents");
                run.Server.ScheduledReload.Should().BeSameAs(reloadBefore);
                peer.Fault.Should().BeNull("the peer served the session without failing");
            }
        }
        finally
        {
            run.Server.Queues.CallerJoined -= onJoined;
            gate.Release();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldReturnAndStillAnnounceWhatItAdmitted_WhenItsTokenIsCancelledAfterTheSnapshotWasApplied()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        var barrierClock = new FakeTimeProvider(run.Clock.GetUtcNow());
        run.Server.BarrierClock = barrierClock;
        using var cts = new CancellationTokenSource();
        var announced = new TaskCompletionSource<AsteriskQueueEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string, AsteriskQueueEntry> onJoined = (queue, entry) =>
        {
            if (queue == Sales)
                announced.TrySetResult(entry);
        };
        // The token is cancelled while the snapshot is applied, after the last check that could still stop it.
        Action<string, AsteriskQueueMember> onMember = (queue, _) =>
        {
            if (queue == Sales)
                cts.Cancel();
        };
        run.Server.Queues.CallerJoined += onJoined;
        run.Server.Queues.MemberAdded += onMember;
        try
        {
            using var counters = new LiveCounters();
            // The pump is held, so the refresh's wait for it runs out: it admits now and announces once the pump catches up.
            await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
            await gate.Held.WaitAsync(Run.Bound);
            peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [Waiting])];
            var refresh = run.Server.ReconcileQueuesAsync(cts.Token).AsTask();
            var timer = await barrierClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
            barrierClock.Advance(timer.DueTime);
            var outcome = await Record.ExceptionAsync(() => refresh.WaitAsync(Run.Bound));
            gate.Release();
            var entry = await CompletesWithinBoundAsync(announced.Task) ? await announced.Task : null;

            using (new AssertionScope())
            {
                cts.IsCancellationRequested.Should().BeTrue("the token was cancelled while the snapshot was applied");
                outcome.Should().BeNull("a snapshot already applied is not reported as a cancelled read");
                Held(run, Sales, "the applied snapshot stays applied").Entries.Keys.Should().Equal([Waiting.Channel]);
                entry.Should().NotBeNull("the caller it admitted is announced once the pump caught up");
                entry?.Channel.Should().Be(Waiting.Channel);
                counters.Joined.Should().Be(1, "and counted joined once");
                HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
            }
        }
        finally
        {
            run.Server.Queues.CallerJoined -= onJoined;
            run.Server.Queues.MemberAdded -= onMember;
            gate.Release();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldCloseOnlyTheVisitItNoLongerLists_WhenASessionManagerIsAttached()
    {
        const string GoneUid = "1790633900.21";
        const string GoneChannel = "PJSIP/far-00000021";
        const string StillUid = "1790633900.22";
        const string StillChannel = "PJSIP/far-00000022";
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        await using var manager = new CallSessionManager(Options.Create(new SessionOptions()),
            NullLogger<CallSessionManager>.Instance, new NullStore(), run.Clock);
        manager.AttachToServer(run.Server, "srv-queue-refresh");
        var left = new System.Collections.Concurrent.ConcurrentQueue<QueueVisitSignal>();
        manager.VisitLeft += left.Enqueue;
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tailSubscription = run.Connection.Subscribe(new TailObserver("visits-open", tail));

        // Two callers join the load's queue live, each with an open visit.
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(GoneUid, GoneChannel, GoneUid));
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(StillUid, StillChannel, StillUid));
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(GoneUid, GoneChannel, BootingAsterisk.QueueName, 1, 1));
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(StillUid, StillChannel, BootingAsterisk.QueueName, 2, 2));
        await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", "visits-open")]);
        await tail.Task.WaitAsync(Run.Bound);
        var gone = manager.GetByChannelId(GoneUid);
        var still = manager.GetByChannelId(StillUid);
        gone.Should().NotBeNull("the manager opened a session for the first caller");
        still.Should().NotBeNull("and for the second");

        // The refresh lists the second caller only: the first left while no leave reached Live.
        peer.QueueEntries = [new QueueCaller(StillChannel, StillUid, Position: 1, Wait: 3)];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            left.Should().ContainSingle("a completed refresh closes the visit it no longer lists, and only that one")
                .Which.Should().Match<QueueVisitSignal>(v => v.SessionId == gone!.SessionId && v.LeaveMissed
                    && v.EventsMayHaveBeenLost && v.QueueName == BootingAsterisk.QueueName);
            Held(run, BootingAsterisk.QueueName, "the queue is still listed").Entries.Keys.Should().Equal([StillChannel]);
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldLetTheCallerItAdmittedLeaveAtItsHangup_WhenTheQueueWasCreatedAfterTheLoad()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [Waiting])];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);
        var admitted = Held(run, Sales, "the refresh admits the new queue").Entries[Waiting.Channel];
        run.Server.Queues.HeldUniqueIdCounts().Should().ContainKey(Waiting.UniqueId, "the admitted caller is held by its Uniqueid");
        using var counters = new LiveCounters();

        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Waiting.UniqueId, Waiting.Channel, Waiting.UniqueId));
        await DispatchedAsync(run, peer, "after-hangup");

        using (new AssertionScope())
        {
            Held(run, Sales, "the queue stays").Entries.Should().BeEmpty("the caller leaves the table at its Hangup");
            admitted.LeaveNotObserved.Should().BeTrue("with no leave observed");
            counters.Left.Should().Be(1, "counted left once");
            counters.WaitSamples.Should().Be(0, "with no wait sample");
            run.Server.Queues.HeldUniqueIdCounts().Should().NotContainKey(Waiting.UniqueId, "no Uniqueid remains held for it");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldHoldNoUniqueIdForTheCallersAndQueueItDrops_WhenALaterHangupNamesThem()
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true, QueueEntries = [HeldCaller],
            OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [Waiting])],
        };
        await using var run = await Run.StartAsync(peer);
        run.Server.Queues.HeldUniqueIdCounts().Should().HaveCount(2, "the load holds both callers");
        var left = new System.Collections.Concurrent.ConcurrentQueue<string>();
        run.Server.Queues.CallerLeft += (_, entry) => left.Enqueue(entry.Channel);

        // The load's queue is gone, and the caller of the kept queue left with no leave observed.
        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().BeNull("the refresh removed the omitted queue");
            Held(run, Sales, "the listed queue stays").Entries.Should().BeEmpty("and dropped its omitted caller");
            run.Server.Queues.HeldUniqueIdCounts().Should().BeEmpty("no Uniqueid remains held for a dropped caller");
            left.Should().BeEquivalentTo([HeldCaller.Channel, Waiting.Channel], "each dropped caller left once");
        }

        using var counters = new LiveCounters();
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(HeldCaller.UniqueId, HeldCaller.Channel, HeldCaller.UniqueId));
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Waiting.UniqueId, Waiting.Channel, Waiting.UniqueId));
        await DispatchedAsync(run, peer, "after-hangups");

        using (new AssertionScope())
        {
            counters.Left.Should().Be(0, "a later Hangup of a dropped caller changes nothing");
            left.Should().HaveCount(2, "and announces no second leave");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldNotBringBackTheCaller_WhenItsHangupArrivesAfterTheListingReadItsRow()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        using var counters = new LiveCounters();

        // Asterisk wrote the caller's row, then the caller hung up, then the listing completed.
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [Waiting])];
        peer.BeforeQueueStatusComplete = () =>
            peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Waiting.UniqueId, Waiting.Channel, Waiting.UniqueId));
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            Held(run, Sales, "the listing still admits the queue").Entries.Should().BeEmpty(
                "a row read before the caller's Hangup does not bring it back");
            run.Server.Queues.HeldUniqueIdCounts().Should().NotContainKey(Waiting.UniqueId, "and the record does not count it");
            counters.Joined.Should().Be(0, "no join is counted for it");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldKeepOneHeldCountForTheCaller_WhenTheRefreshListsItUnderANewChannelName()
    {
        const string Renamed = "PJSIP/far-00000001<MASQ>";
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        using var counters = new LiveCounters();

        peer.QueueEntries = [HeldCaller with { Channel = Renamed }];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            Held(run, BootingAsterisk.QueueName, "the queue is listed").Entries.Keys.Should().Equal([Renamed],
                "the caller is re-keyed under the name the listing reports");
            run.Server.Queues.HeldUniqueIdCounts().Should().BeEquivalentTo(
                new Dictionary<string, int> { [HeldCaller.UniqueId] = 1 }, "the re-keyed caller keeps one count");
            counters.Joined.Should().Be(0, "it is the same caller");
            counters.Left.Should().Be(0);
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    /// <summary>Sends a user event after the frames sent so far and waits until the connection has dispatched it.</summary>
    private static async Task DispatchedAsync(Run run, BootingAsterisk peer, string marker)
    {
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = run.Connection.Subscribe(new TailObserver(marker, tail));
        await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", marker)]);
        await tail.Task.WaitAsync(Run.Bound);
    }

    private static void AssertNothingRemoved(Run run, BootingAsterisk peer, LiveCounters counters, Exception? outcome,
        Type? expectFailure)
    {
        var queue = Held(run, BootingAsterisk.QueueName, "an unfinished response is no evidence that the queue is gone");
        using (new AssertionScope())
        {
            if (expectFailure is null)
                outcome.Should().BeNull("a refused QueueStatus does not make the refresh throw");
            else
                outcome.Should().BeAssignableTo(expectFailure);
            queue.Entries.Should().ContainKey(HeldCaller.Channel, "nor that its caller left");
            queue.Members.Should().ContainKey(BootingAsterisk.MemberLocation, "nor that its member was removed");
            counters.Left.Should().Be(0, "no caller is counted left");
            run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0, "the read's window is closed");
            run.Server.Queues.HeldUniqueIdCounts().Should().BeEquivalentTo(
                new Dictionary<string, int> { [HeldCaller.UniqueId] = 1 }, "the held-caller record is unchanged");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    /// <summary>The queue the table holds under <paramref name="name"/>; fails the test, saying why, when it holds none.</summary>
    private static AsteriskQueue Held(Run run, string name, string because) =>
        run.Server.Queues.GetByName(name) ?? throw new Xunit.Sdk.XunitException($"Expected queue {name} to be held: {because}.");

    private static AskCounts Asks(BootingAsterisk peer) => new(peer.Asked("Status"), peer.Asked("BridgeList"),
        peer.Asked("ConfbridgeListRooms"), peer.Asked("QueueStatus"), peer.Asked("Agents"));

    private sealed record AskCounts(int Status, int BridgeList, int ConfbridgeListRooms, int QueueStatus, int Agents);

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private sealed class TailObserver(string userEvent, TaskCompletionSource reached) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is UserEventEvent { UserEvent: var name } && name == userEvent)
                reached.TrySetResult();
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;
        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<CallSession?>(null);
    }
}
